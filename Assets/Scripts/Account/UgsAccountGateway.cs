using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Unity.Services.Authentication;
using Unity.Services.CloudCode;
using Unity.Services.CloudSave;
using Unity.Services.CloudSave.Models.Data.Player;
using Unity.Services.Core;
using Unity.Services.Core.Environments;

/// <summary>UGS boundary. Tests replace this boundary, never the scene flow or AccountClient.</summary>
public interface IUgsAccountGateway : IDisposable
{
    bool IsSignedIn { get; }
    string PlayerId { get; }
    int PublicUid { get; }
    bool HasLegacyUid { get; }
    string AccessToken { get; }
    event Action SessionExpired;
    Task InitializeAsync();
    Task SignUpAsync(string username, string password);
    Task SignInAsync(string username, string password);
    void SignOut();
    Task<PlayerProfileData> LoadProfileAsync(string username);
    Task<PlayerProfileData> SaveProfileAsync(string username, int revision, CharacterAppearanceData appearance, PvpLoadoutData loadout);
    Task<PlayerProfileData> SaveAvatarAsync(string username, int revision, byte avatarId);
}

public sealed class UgsAccountException : Exception
{
    public readonly bool SessionInvalid;
    public UgsAccountException(string message, bool sessionInvalid = false) : base(message) => SessionInvalid = sessionInvalid;
}

public interface IUgsPersonalProfileGateway
{
    Task<PlayerProfileData> SavePersonalProfileAsync(string username, int revision, PersonalProfileData personal);
}

public sealed partial class UgsAccountGateway : IUgsAccountGateway, IUgsPvpGateway, IUgsPersonalProfileGateway
{
    public const string ProfileKey = "fps_profile_v1";
    public const string PersonalKey = "fps_personal_v1";
    public const string CareerKey = "fps_career_v1";
    private PlayerCareerRecord career;
    private PersonalProfileData personalProfile;
    private int personalRevision;
    public const string UidScript = "FpsAccountUid";
    public int PublicUid { get; private set; }
    public bool HasLegacyUid { get; private set; }
    private static SemaphoreSlim authGate = new SemaphoreSlim(1, 1);
    private static Task initialization;
    private static string initializedEnvironment;
    private static UgsAccountGateway owner;
    private bool disposed, subscribed, authInFlight;
    private string script;
    public event Action SessionExpired;

    public bool IsSignedIn => !disposed && owner == this && UnityServices.State == ServicesInitializationState.Initialized &&
        AuthenticationService.Instance.IsAuthorized;
    public string PlayerId => IsSignedIn ? AuthenticationService.Instance.PlayerId : "";
    public string AccessToken => IsSignedIn ? AuthenticationService.Instance.AccessToken : "";

    public async Task InitializeAsync()
    {
        ThrowIfDisposed();
        if (!AccountServiceSettings.TryGetUgsConfiguration(out string environment, out script, out string error))
            throw new UgsAccountException(error);
        if (initialization == null || initialization.IsFaulted || initialization.IsCanceled ||
            (initialization.IsCompleted && UnityServices.State == ServicesInitializationState.Uninitialized))
        {
            if (UnityServices.State == ServicesInitializationState.Initialized &&
                initializedEnvironment != null && initializedEnvironment != environment)
                throw new UgsAccountException("UGS 环境已初始化，修改环境后请退出并重新启动游戏。");
            initializedEnvironment = environment;
            initialization = UnityServices.State == ServicesInitializationState.Initialized ? Task.CompletedTask :
                UnityServices.InitializeAsync(new InitializationOptions().SetEnvironmentName(environment));
        }
        else if (initializedEnvironment != environment)
            throw new UgsAccountException("UGS 环境已初始化，修改环境后请退出并重新启动游戏。");
        await initialization;
        ThrowIfDisposed();
        if (!subscribed)
        {
            AuthenticationService.Instance.Expired += OnExpired;
            subscribed = true;
        }
    }

    public Task SignUpAsync(string username, string password) => AuthenticateAsync(username, password, true);
    public Task SignInAsync(string username, string password) => AuthenticateAsync(username, password, false);

    private async Task AuthenticateAsync(string username, string password, bool registering)
    {
        await InitializeAsync();
        var gate = authGate;
        await gate.WaitAsync();
        bool accountCreated = false;
        try
        {
            ThrowIfDisposed();
            // Explicit login only: never silently recover a cached player or create an anonymous account.
            AuthenticationService.Instance.SignOut(true);
            owner = this;
            PublicUid = 0; HasLegacyUid = false;
            authInFlight = true;
            if (registering)
            {
                await AuthenticationService.Instance.SignUpWithUsernamePasswordAsync(username, password);
                accountCreated = true;
            }
            else await AuthenticationService.Instance.SignInWithUsernamePasswordAsync(username, password);
            ThrowIfDisposed();
            if (!registering)
            {
                var info = AuthenticationService.Instance.PlayerInfo;
                if (info == null || !info.CreatedAt.HasValue) info = await AuthenticationService.Instance.GetPlayerInfoAsync();
                RequireSession();
                if (info == null || info.Id != PlayerId || !info.CreatedAt.HasValue)
                    throw new UgsAccountException("未能读取账号注册时间，请稍后重新登录。");
                HasLegacyUid = AccountUid.IsLegacyRegistration(info.CreatedAt.Value);
            }
            // Existing accounts keep their original display identity without UID writes.
            // New registrations, and later logins of those accounts, share one immutable allocation.
            if (!HasLegacyUid) await EnsurePublicUidAsync();
        }
        catch (Exception error)
        {
            if (!(error is OperationCanceledException))
            {
                int code = error is RequestFailedException request ? request.ErrorCode : -1;
                // Preserve the service failure category for troubleshooting without logging
                // the SDK exception, credentials, request body, or authorization headers.
                string category = IsWrongUsernameOrPassword(error) ? "invalid_credentials" :
                    IsEnvironmentNameInvalid(error) ? "invalid_environment" :
                    IsPasswordProviderDisabled(error) ? "provider_disabled" :
                    code == AuthenticationErrorCodes.InvalidParameters ? "invalid_parameters" : "service_request_failed";
                UnityEngine.Debug.LogWarning("[UGS Authentication] Request failed; operation=" +
                    (registering ? "register" : "login") + "; code=" + code + "; category=" + category);
            }
            TrySignOutOwnedSession();
            if (accountCreated && !(error is OperationCanceledException))
                throw new UgsAccountException("账号已注册，但数字 UID 暂未就绪，请登录重试。" + FriendlyError(error));
            throw;
        }
        finally
        {
            authInFlight = false;
            if (disposed) TrySignOutOwnedSession();
            gate.Release();
        }
    }

    public void SignOut() => SignOutOwnedSession();
    private void SignOutOwnedSession()
    {
        PublicUid = 0;
        HasLegacyUid = false;
        if (owner != this) return;
        try
        {
            if (UnityServices.State == ServicesInitializationState.Initialized) AuthenticationService.Instance.SignOut(true);
        }
        finally { owner = null; }
    }
    private void TrySignOutOwnedSession() { try { SignOutOwnedSession(); } catch { /* Preserve the original failure during cleanup. */ } }

    public async Task<PlayerProfileData> LoadProfileAsync(string username)
    {
        RequireSession();
        string playerId = PlayerId;
        // Read exactly the Protected bucket written by FpsProfile. Missing data is a virtual default,
        // produced by Cloud Code without writing, so a late load cannot overwrite another device's save.
        var data = await CloudSaveService.Instance.Data.Player.LoadAsync(new HashSet<string> { ProfileKey, PersonalKey, CareerKey },
            new Unity.Services.CloudSave.Models.Data.Player.LoadOptions(new ProtectedReadAccessClassOptions()));
        RequireSamePlayer(playerId);
        career = data.TryGetValue(CareerKey, out var careerItem) ? ReadCareer(careerItem.Value.GetAs<JObject>()) : new PlayerCareerRecord();
        personalProfile = null; personalRevision = 0;
        if (data.TryGetValue(PersonalKey, out var personalItem))
        {
            var personalJson = personalItem.Value.GetAs<JObject>();
            RequireFields(personalJson, new[] { "revision" }, JTokenType.Integer);
            RequireFields(personalJson, new[] { "personal" }, JTokenType.Object);
            var details = (JObject)personalJson["personal"];
            RequireFields(details, new[] { "displayName", "signature" }, JTokenType.String);
            RequireFields(details, new[] { "gender" }, JTokenType.Integer);
            personalProfile = details.ToObject<PersonalProfileData>();
            personalRevision = personalJson["revision"].Value<int>();
            if (personalRevision < 0 || !PersonalProfileData.IsValid(personalProfile)) throw new UgsAccountException("云端个人资料格式异常。");
        }
        if (data.TryGetValue(ProfileKey, out var item))
        {
            var json = item.Value.GetAs<JObject>();
            // DTO initializers supply defaults for missing JSON members; reject those incomplete
            // stored records before deserialization can disguise them as valid saved appearances.
            RequireFields(json, new[] { "accountId", "username" }, JTokenType.String);
            RequireFields(json, new[] { "revision", "avatarId" }, JTokenType.Integer);
            RequireFields(json, new[] { "appearance", "loadout" }, JTokenType.Object);
            RequireFields(json, new[] { "match" }, JTokenType.Null);
            RequireFields((JObject)json["appearance"], new[] { "outfitId", "faceId", "clothColorId", "hairColorId" }, JTokenType.Integer);
            RequireFields((JObject)json["appearance"], new[] { "hat", "accessories" }, JTokenType.Boolean);
            RequireFields((JObject)json["loadout"], new[] { "primaryWeaponId", "primarySightId", "sidearmSightId" }, JTokenType.Integer);
            var profile = json.ToObject<PlayerProfileData>();
            profile.uid = PublicUid;
            profile.personal = personalProfile; profile.personalRevision = personalRevision; profile.career = career;
            return profile;
        }
        return await CallProfileAsync(new Dictionary<string, object> { { "operation", "load" }, { "username", username } });
    }

    public Task<PlayerProfileData> SaveProfileAsync(string username, int revision, CharacterAppearanceData appearance, PvpLoadoutData loadout)
        => CallProfileAsync(new Dictionary<string, object>
        {
            { "operation", "save" }, { "username", username }, { "revision", revision },
            { "appearance", appearance }, { "loadout", loadout }
        });

    public Task<PlayerProfileData> SaveAvatarAsync(string username, int revision, byte avatarId)
        => CallProfileAsync(new Dictionary<string, object>
        {
            { "operation", "avatar" }, { "username", username }, { "revision", revision }, { "avatarId", avatarId }
        });

    public Task<PlayerProfileData> SavePersonalProfileAsync(string username, int revision, PersonalProfileData personal)
        => CallProfileAsync(new Dictionary<string, object>
        {
            { "operation", "personal" }, { "username", username }, { "revision", revision }, { "personal", personal }
        });

    private async Task<PlayerProfileData> CallProfileAsync(Dictionary<string, object> arguments)
    {
        RequireSession();
        string playerId = PlayerId;
        var result = await CloudCodeService.Instance.CallEndpointAsync<ProfileResponse>(script, arguments);
        RequireSamePlayer(playerId);
        if (result == null) throw new UgsAccountException("云存档返回为空，请检查 Cloud Code 脚本和发布环境。");
        if (!result.ok) throw new UgsAccountException(ProfileError(result.code), result.code == "authentication_required");
        if (result.profile != null)
        {
            result.profile.uid = PublicUid;
            if ((string)arguments["operation"] == "personal")
            { personalProfile = result.profile.personal; personalRevision = result.profile.personalRevision; }
            result.profile.personal = personalProfile; result.profile.personalRevision = personalRevision; result.profile.career = career;
        }
        return result.profile;
    }

    private static PlayerCareerRecord ReadCareer(JObject json)
    {
        RequireFields(json, new[] { "matches", "wins", "kills", "deaths", "headshotKills" }, JTokenType.Integer);
        var result = json.ToObject<PlayerCareerRecord>();
        if (result == null || !result.IsValid) throw new UgsAccountException("云端生涯记录格式异常。");
        return result;
    }

    private async Task EnsurePublicUidAsync()
    {
        RequireSession();
        string playerId = PlayerId;
        UidResponse result;
        try
        {
            result = await CloudCodeService.Instance.CallEndpointAsync<UidResponse>(UidScript,
                new Dictionary<string, object> { { "operation", "ensure" } });
        }
        catch (CloudCodeException error) when (error.Reason == CloudCodeExceptionReason.NotFound)
        { throw new UgsAccountException("数字 UID 服务尚未发布，请检查 FpsAccountUid 脚本和环境。"); }
        RequireSamePlayer(playerId);
        if (result == null || !result.ok || !AccountUid.IsValid(result.uid))
        {
            string message = result != null && result.code == "uid_setup_required" ? "数字 UID 服务尚未初始化，请联系管理员。" :
                result != null && result.code == "uid_corrupt" ? "数字 UID 数据异常，请联系管理员。" :
                result != null && result.code == "uid_exhausted" ? "账号 UID 编号已用尽，请联系管理员。" :
                "数字 UID 暂时不可用，请稍后重试。";
            throw new UgsAccountException(message, result != null && result.code == "authentication_required");
        }
        PublicUid = result.uid;
    }

    [Serializable, UnityEngine.Scripting.Preserve] private sealed class UidResponse
    {
        public bool ok;
        public int uid;
        public string code;
    }

    [Serializable, UnityEngine.Scripting.Preserve] private sealed class ProfileResponse
    {
        public bool ok;
        public string code;
        public PlayerProfileData profile;
    }

    private static string ProfileError(string code)
    {
        switch (code)
        {
            case "profile_conflict": return "档案已在其他设备更新，请重新读取后再保存。";
            case "invalid_profile": return "外观或配装包含无效选项，未保存。";
            case "invalid_avatar": return "头像选项无效，未保存。";
            case "profile_corrupt": return "云存档格式异常，已停止写入，请联系管理员检查。";
            case "authentication_required": return "登录已失效，请重新登录。";
            case "rate_limited": return "云存档请求过于频繁，请稍后重试。";
            case "service_misconfigured": return "云存档权限未配置，请检查 Cloud Save 与 Cloud Code 服务。";
            default: return "云存档请求失败，请检查 Cloud Code 日志和服务配置后重试。";
        }
    }

    private void OnExpired()
    {
        PublicUid = 0;
        if (owner == this && !disposed) SessionExpired?.Invoke();
    }
    private void RequireSession()
    {
        ThrowIfDisposed();
        if (!IsSignedIn) throw new UgsAccountException("登录已失效，请重新登录。", true);
    }
    private void RequireSamePlayer(string playerId)
    {
        RequireSession();
        if (PlayerId != playerId) throw new UgsAccountException("账号已切换，请重新读取用户数据。", true);
    }
    private void ThrowIfDisposed() { if (disposed) throw new OperationCanceledException(); }

    public void Dispose()
    {
        disposed = true;
        if (subscribed && UnityServices.State == ServicesInitializationState.Initialized)
            AuthenticationService.Instance.Expired -= OnExpired;
        subscribed = false;
        // Keep ownership while an SDK authentication call is pending. Its continuation cleans up
        // before AuthGate permits a new owner to sign in.
        if (!authInFlight) TrySignOutOwnedSession();
    }

    private static void RequireFields(JObject value, string[] fields, JTokenType type)
    {
        if (value == null) throw new UgsAccountException("云存档格式异常，未应用或覆盖数据。");
        foreach (string field in fields)
            if (value[field] == null || value[field].Type != type)
                throw new UgsAccountException("云存档格式异常，未应用或覆盖数据。");
    }

    [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        authGate = new SemaphoreSlim(1, 1);
        initialization = null;
        initializedEnvironment = null;
        owner = null;
    }

    public static string FriendlyError(Exception error)
    {
        if (error is UgsAccountException known) return known.Message;
        if (error is OperationCanceledException) return "账号请求已取消，请重新操作。";
        if (IsWrongUsernameOrPassword(error)) return "用户名或密码不正确，请核对后重试。";
        if (error is AuthenticationException authentication)
        {
            if (authentication.ErrorCode == AuthenticationErrorCodes.AccountAlreadyLinked)
                return "该用户名已被使用，请换一个用户名，或返回登录。";
            if (authentication.ErrorCode == AuthenticationErrorCodes.InvalidParameters && IsEnvironmentNameInvalid(authentication))
                return "UGS 环境名无效，请检查环境配置。";
            if (authentication.ErrorCode == AuthenticationErrorCodes.InvalidProvider ||
                (authentication.ErrorCode == AuthenticationErrorCodes.InvalidParameters && IsPasswordProviderDisabled(authentication)))
                return "请在 UGS 控制台启用用户名密码登录。";
            if (authentication.ErrorCode == AuthenticationErrorCodes.InvalidParameters)
                return "UGS 请求参数或服务配置无效（10002）。";
            if (authentication.ErrorCode == AuthenticationErrorCodes.BannedUser)
                return "账号暂时无法登录，请联系管理员。";
            if (authentication.ErrorCode == CommonErrorCodes.TransportError || authentication.ErrorCode == CommonErrorCodes.Timeout)
                return "无法连接 UGS 登录服务，请检查网络后重试。";
            if (authentication.ErrorCode == CommonErrorCodes.TooManyRequests)
                return "登录请求过于频繁，请稍后重试。";
            return "登录未成功，请核对账号和密码；没有账号请点击注册。";
        }
        if (error is CloudCodeException cloudCode && cloudCode.Reason == CloudCodeExceptionReason.NotFound && cloudCode.ErrorCode == 9002)
            return "找不到云存档脚本，请检查名称与发布环境（9002）。";
        if (IsSessionInvalid(error)) return "登录已失效，请重新登录。";
        if (error is RequestFailedException request)
        {
            if (request.ErrorCode == 429 || request.ErrorCode == CommonErrorCodes.TooManyRequests)
                return "请求过于频繁，请稍后重试。";
            return "无法完成 UGS 请求，请检查网络、项目关联、服务开通及 Cloud Code 发布环境（错误码 " + request.ErrorCode + "）。";
        }
        return "UGS 请求失败，请检查服务配置后重试。";
    }

    public static bool IsSessionInvalid(Exception error)
    {
        if (error is UgsAccountException known && known.SessionInvalid) return true;
        if (error is CloudSaveException save && (save.Reason == CloudSaveExceptionReason.Unauthorized || save.Reason == CloudSaveExceptionReason.AccessTokenMissing)) return true;
        if (error is CloudCodeException code && (code.Reason == CloudCodeExceptionReason.Unauthorized || code.Reason == CloudCodeExceptionReason.AccessTokenMissing)) return true;
        return error is RequestFailedException request && (request.ErrorCode == CommonErrorCodes.InvalidToken || request.ErrorCode == CommonErrorCodes.TokenExpired);
    }

    private static bool IsPasswordProviderDisabled(Exception error)
    {
        string message = (error.Message ?? "").ToLowerInvariant();
        bool provider = message.Contains("provider") || (message.Contains("username") && message.Contains("password"));
        return provider && (message.Contains("not enabled") || message.Contains("not configured") || message.Contains("disabled"));
    }

    private static bool IsEnvironmentNameInvalid(Exception error)
    {
        // Authentication maps several server failures to 10002. Match the observed service
        // detail instead of blaming credentials or silently selecting another save environment.
        return (error.Message ?? "").IndexOf("Invalid environment name provided", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsWrongUsernameOrPassword(Exception error)
    {
        // Authentication 3.6.1 maps the observed WRONG_USERNAME_PASSWORD title to Unknown (0).
        // Recognize only its structured HTTP 400 response; unrelated Unknown errors stay unknown.
        if (!(error is RequestFailedException request) || request.ErrorCode != CommonErrorCodes.Unknown ||
            (error.GetType() != typeof(RequestFailedException) && !(error is AuthenticationException)) ||
            error.InnerException == null ||
            error.InnerException.GetType().FullName != "Unity.Services.Authentication.WebRequestException")
            return false;
        try
        {
            JObject response = JObject.Parse(error.InnerException.Message);
            return response["title"]?.Type == JTokenType.String &&
                (string)response["title"] == "WRONG_USERNAME_PASSWORD" &&
                response["status"]?.Type == JTokenType.Integer &&
                response["status"].ToString(Newtonsoft.Json.Formatting.None) == "400";
        }
        catch (Newtonsoft.Json.JsonException) { return false; }
    }
}
