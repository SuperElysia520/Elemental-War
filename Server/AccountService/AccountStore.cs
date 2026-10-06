using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Fps.AccountService;

/// <summary>Single service process, durable atomic snapshots. The exclusive lock forbids two writers.</summary>
public sealed partial class AccountStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object gate = new();
    private readonly string path;
    private readonly FileStream processLock;
    private readonly TimeSpan lifetime;
    private readonly PasswordHasher<string> passwords = new(Options.Create(new PasswordHasherOptions
    {
        CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
        IterationCount = 210_000
    }));
    private readonly string dummyHash;
    private Dictionary<string, Account> accounts;
    private readonly Dictionary<string, (string SessionHash, DateTimeOffset Expires, MatchAdmission? Match)> tickets = new(StringComparer.Ordinal);

    public AccountStore(IConfiguration configuration, IWebHostEnvironment environment)
    {
        path = Path.GetFullPath(configuration["AccountStorePath"] ?? "data/accounts.json", environment.ContentRootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        processLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var minutes = configuration.GetValue("SessionLifetimeMinutes", 720d);
        if (minutes <= 0 || minutes > 10_080) throw new InvalidOperationException("SessionLifetimeMinutes must be in (0, 10080].");
        lifetime = TimeSpan.FromMinutes(minutes);
        dummyHash = passwords.HashPassword("dummy", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        if (File.Exists(path))
        {
            var data = JsonSerializer.Deserialize<Database>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("Account database is empty; refusing to overwrite it.");
            if (data.FormatVersion != 1 || data.Accounts == null)
                throw new InvalidDataException("Unsupported account database format; refusing to overwrite it.");
            if (data.Accounts.Any(a => a == null || !Guid.TryParseExact(a.Id, "N", out _) || a.Username == null
                || !UsernamePattern().IsMatch(a.Username) || string.IsNullOrEmpty(a.PasswordHash)
                || a.Profile == null || a.Profile.AccountId != a.Id || a.Profile.Username != a.Username
                || a.Profile.Revision < 0 || a.Profile.Appearance == null || !a.Profile.Appearance.IsValid
                || a.Profile.Loadout == null || !a.Profile.Loadout.IsValid || a.Sessions == null
                || a.Sessions.Any(s => s == null || s.TokenHash == null || s.TokenHash.Length != 64)))
                throw new InvalidDataException("Account database contains invalid records; refusing to overwrite it.");
            accounts = data.Accounts.ToDictionary(a => a.Username.ToUpperInvariant(), StringComparer.Ordinal);
        }
        else accounts = new(StringComparer.Ordinal);
    }

    public AuthResponse Register(Credentials credentials)
    {
        var (username, password) = ValidateCredentials(credentials, registering: true);
        var key = username.ToUpperInvariant();
        // Expensive password hashing happens outside the shared storage lock.
        var hash = passwords.HashPassword(username, password);
        lock (gate)
        {
            if (accounts.ContainsKey(key)) throw new ApiException(409, "username_taken", "用户名已被使用。");
            var id = Guid.NewGuid().ToString("N");
            var account = new Account(id, username, hash,
                new Profile(id, username, 0, new Appearance(), new Loadout()), []);
            return IssueSession(key, account);
        }
    }

    public AuthResponse Login(Credentials credentials)
    {
        var (username, password) = ValidateCredentials(credentials, registering: false);
        var key = username.ToUpperInvariant();
        Account? account;
        lock (gate) accounts.TryGetValue(key, out account);
        var result = passwords.VerifyHashedPassword(username, account?.PasswordHash ?? dummyHash, password);
        if (account == null || result == PasswordVerificationResult.Failed)
            throw new ApiException(401, "invalid_credentials", "用户名或密码不正确。");
        var rehashed = result == PasswordVerificationResult.SuccessRehashNeeded ? passwords.HashPassword(username, password) : null;
        lock (gate)
        {
            account = accounts[key];
            if (rehashed != null) account = account with { PasswordHash = rehashed };
            return IssueSession(key, account);
        }
    }

    public Profile GetProfile(string token)
    {
        lock (gate) return Resolve(token).account.Profile;
    }

    public Profile GetStoredProfile(string accountId)
    {
        lock (gate) return accounts.Values.Single(a => a.Id == accountId).Profile;
    }

    public Profile SaveProfile(string token, SaveProfileRequest request)
    {
        lock (gate)
        {
            var (key, account) = Resolve(token);
            if (request.Appearance == null || !request.Appearance.IsValid || request.Loadout == null || !request.Loadout.IsValid)
                throw new ApiException(400, "invalid_profile", "外观或枪械配装包含无效选项。");
            if (request.Revision != account.Profile.Revision)
                throw new ApiException(409, "profile_conflict", "档案已在其他设备更新，请重新加载后再保存。");
            var profile = account.Profile with
            {
                Revision = checked(account.Profile.Revision + 1), Appearance = request.Appearance, Loadout = request.Loadout
            };
            Persist(key, account with { Profile = profile });
            return profile;
        }
    }

    public void Logout(string token)
    {
        lock (gate)
        {
            var (key, account) = Resolve(token);
            var hash = HashToken(token);
            Persist(key, account with { Sessions = account.Sessions.Where(s => s.TokenHash != hash).ToList() });
        }
    }

    public Profile SaveAvatar(string token, byte avatarId)
    {
        if (avatarId >= 32) throw new ApiException(400, "invalid_avatar", "头像选项无效。");
        lock (gate)
        {
            var (key, account) = Resolve(token);
            var profile = account.Profile with { AvatarId = avatarId, Revision = checked(account.Profile.Revision + 1) };
            Persist(key, account with { Profile = profile });
            return profile;
        }
    }

    public GameTicketResponse CreateGameTicket(string token, MatchAdmission? match = null)
    {
        lock (gate)
        {
            Resolve(token);
            var hash = HashToken(token);
            var now = DateTimeOffset.UtcNow;
            // Bound memory and revoke earlier join attempts for this login before creating a new one.
            foreach (var key in tickets.Where(t => t.Value.Expires <= now || t.Value.SessionHash == hash).Select(t => t.Key).ToArray())
                tickets.Remove(key);
            var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var expires = now.AddSeconds(60);
            tickets.Add(HashToken(ticket), (hash, expires, match));
            return new GameTicketResponse(ticket, expires.ToUnixTimeSeconds());
        }
    }

    public Profile RedeemGameTicket(string? ticket)
    {
        lock (gate)
        {
            if (ticket == null || ticket.Length != 64 || !tickets.Remove(HashToken(ticket), out var entry)
                || entry.Expires <= DateTimeOffset.UtcNow)
                throw new ApiException(401, "invalid_ticket", "入场凭证已失效，请重新加入房间。");
            return ResolveHash(entry.SessionHash).account.Profile with { Match = entry.Match };
        }
    }

    private AuthResponse IssueSession(string key, Account account)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var now = DateTimeOffset.UtcNow;
        var expires = now + lifetime;
        var sessions = account.Sessions.Where(s => s.ExpiresAt > now)
            .OrderByDescending(s => s.CreatedAt).Take(4).ToList();
        sessions.Add(new Session(HashToken(token), expires, now));
        Persist(key, account with { Sessions = sessions });
        return new AuthResponse(token, expires.ToUnixTimeSeconds(), account.Profile);
    }

    private (string key, Account account) Resolve(string token)
    {
        if (token.Length != 64 || token.Any(c => !Uri.IsHexDigit(c)))
            throw new ApiException(401, "session_expired", "登录已失效，请重新登录。");
        return ResolveHash(HashToken(token));
    }

    private (string key, Account account) ResolveHash(string hash)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in accounts)
            if (entry.Value.Sessions.Any(s => s.ExpiresAt > now && s.TokenHash == hash))
                return (entry.Key, entry.Value);
        throw new ApiException(401, "session_expired", "登录已失效，请重新登录。");
    }

    private void Persist(string key, Account account)
    {
        var next = new Dictionary<string, Account>(accounts, StringComparer.Ordinal) { [key] = account };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Database(1, next.Values.ToList()), Json);
        // Same-directory rename prevents partial snapshots. Memory changes only after successful disk replacement.
        var temporary = path + ".pending";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
        accounts = next;
    }

    private static (string username, string password) ValidateCredentials(Credentials credentials, bool registering)
    {
        var username = credentials.Username?.Trim() ?? "";
        var password = credentials.Password ?? "";
        if (!UsernamePattern().IsMatch(username))
            throw new ApiException(400, "invalid_username", "用户名需为 3–24 位字母、数字或下划线。");
        if (password.Length < (registering ? 10 : 1) || password.Length > 128)
            throw new ApiException(400, "invalid_password", "密码需为 10–128 个字符。");
        return (username, password);
    }

    [GeneratedRegex("^[a-zA-Z0-9_]{3,24}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();
    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public void Dispose() => processLock.Dispose();
}
