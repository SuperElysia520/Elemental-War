using System;
using System.Threading.Tasks;

public sealed partial class AccountClient
{
    internal static Func<IUgsAccountGateway> UgsGatewayFactory;
    private IUgsAccountGateway ugs;
    private bool ugsDisposed;
    private int ugsSessionGeneration;
    private bool HasUgsSession => !ugsDisposed && ugs != null && ugs.IsSignedIn && !string.IsNullOrEmpty(AccountId) && ugs.PlayerId == AccountId;
    private string UgsAccessToken => HasUgsSession ? ugs.AccessToken : "";

    private IUgsAccountGateway GetUgsGateway()
    {
        if (ugs != null) return ugs;
        ugs = UgsGatewayFactory != null ? UgsGatewayFactory() : new UgsAccountGateway();
        ugs.SessionExpired += UgsSessionExpired;
        return ugs;
    }

    private void RegisterUgs(string username, string password, Action<bool, string> completed)
    {
        if (!ValidateUgsCredentials(username, password, true, completed)) return;
        if (HasSession) { completed?.Invoke(false, "请先切换账号，再注册新账号。"); return; }
        ExecuteUgs(async () =>
        {
            var gateway = GetUgsGateway();
            try { await gateway.SignUpAsync(username.Trim(), password); }
            finally { gateway.SignOut(); }
            return new UgsResult();
        }, result => ClearSession(), completed, true);
    }

    private void AuthenticateUgs(string operation, string username, string password, Action<bool, string> completed, bool applyProfile)
    {
        if (operation == "register") { RegisterUgs(username, password, completed); return; }
        bool registering = operation == "register";
        if (!ValidateUgsCredentials(username, password, registering, completed)) return;
        if (HasSession) { completed?.Invoke(false, "请先切换账号，再登录其他账号。"); return; }
        ExecuteUgs(async () =>
        {
            var gateway = GetUgsGateway();
            if (registering) await gateway.SignUpAsync(username.Trim(), password);
            else await gateway.SignInAsync(username.Trim(), password);
            string id = gateway.PlayerId;
            var profile = applyProfile ? await gateway.LoadProfileAsync(username.Trim()) : null;
            return new UgsResult { accountId = id, username = username.Trim(), profile = profile };
        }, result =>
        {
            if (ugs == null || !ugs.IsSignedIn || ugs.PlayerId != result.accountId || string.IsNullOrEmpty(result.accountId))
                throw new UgsAccountException("登录会话已失效，请重新登录。", true);
            if (!AccountUid.IsValid(ugs.PublicUid) && !(ugs.HasLegacyUid && ugs.PublicUid == 0)) throw new UgsAccountException("数字 UID 未就绪，请重新登录。");
            if (applyProfile) ValidateUgsProfile(result.profile, result.accountId);
            AccountId = result.accountId;
            Username = result.username;
            Profile = result.profile;
            ProfileChanged?.Invoke(Profile);
        }, completed, true);
    }

    private void LoadUgsProfile(Action<bool, string> completed)
    {
        if (!HasSession) { RejectMissingSession(completed); return; }
        string id = AccountId;
        ExecuteUgs(async () => new UgsResult { profile = await GetUgsGateway().LoadProfileAsync(Username) },
            result => ApplyUgsProfile(result.profile, id), completed);
    }

    private void SaveUgsProfile(CharacterAppearanceData appearance, PvpLoadoutData loadout, Action<bool, string> completed)
    {
        if (!IsAuthenticated) { RejectMissingSession(completed); return; }
        string id = AccountId;
        int revision = Profile.revision;
        ExecuteUgs(async () => new UgsResult
        {
            profile = await GetUgsGateway().SaveProfileAsync(Username, revision, appearance.Sanitized(), loadout.Sanitized())
        }, result => ApplyUgsProfile(result.profile, id), completed);
    }

    private void SaveUgsAvatar(byte avatarId, Action<bool, string> completed)
    {
        if (!IsAuthenticated) { RejectMissingSession(completed); return; }
        if (avatarId >= 32) { completed?.Invoke(false, "头像选项无效。"); return; }
        string id = AccountId;
        int revision = Profile.revision;
        ExecuteUgs(async () => new UgsResult { profile = await GetUgsGateway().SaveAvatarAsync(Username, revision, avatarId) },
            result => ApplyUgsProfile(result.profile, id), completed);
    }

    public void SavePersonalProfile(PersonalProfileData personal, Action<bool, string> completed = null)
    {
        if (!IsAuthenticated) { RejectMissingSession(completed); return; }
        if (!PersonalProfileData.IsValid(personal)) { completed?.Invoke(false, "个人资料无效，签名最多80字，请勿输入控制字符或尖括号。"); return; }
        if (personal.displayName != (Profile.personal?.displayName ?? "") && !DisplayNameRules.IsValid(personal.displayName))
        { completed?.Invoke(false, DisplayNameRules.Error); return; }
        if (!UsesUgs || !(GetUgsGateway() is IUgsPersonalProfileGateway gateway))
        { completed?.Invoke(false, "当前账号服务尚未支持修改个人资料。"); return; }
        string id = AccountId;
        int revision = Profile.personalRevision;
        var snapshot = new PersonalProfileData { displayName = personal.displayName, gender = personal.gender, signature = personal.signature };
        ExecuteUgs(async () => new UgsResult { profile = await gateway.SavePersonalProfileAsync(Username, revision, snapshot) },
            result => ApplyUgsProfile(result.profile, id), completed);
    }

    private void LogoutUgs(Action<bool, string> completed)
    {
        if (!Begin(completed)) return;
        ugsSessionGeneration++;
        string error = "";
        try { ugs?.SignOut(); }
        catch (Exception exception) { error = UgsAccountGateway.FriendlyError(exception); }
        ClearSession();
        Finish(error, completed);
    }

    private async void ExecuteUgs(Func<Task<UgsResult>> operation, Action<UgsResult> apply,
        Action<bool, string> completed, bool clearOnFailure = false)
    {
        if (!Begin(completed)) return;
        int generation = ugsSessionGeneration;
        string error = "";
        try
        {
            UgsResult result = await operation();
            if (this == null || ugsDisposed) return;
            if (generation != ugsSessionGeneration) throw new UgsAccountException("登录已失效，请重新登录。", true);
            apply(result);
        }
        catch (Exception exception)
        {
            if (this == null || ugsDisposed) return;
            error = UgsAccountGateway.FriendlyError(exception);
            if (clearOnFailure || UgsAccountGateway.IsSessionInvalid(exception) || (ugs != null && !ugs.IsSignedIn))
            {
                try { ugs?.SignOut(); } catch { /* Always release busy state even if SDK cleanup fails. */ }
                ClearSession();
            }
        }
        if (this != null && !ugsDisposed) Finish(error, completed);
    }

    private void ApplyUgsProfile(PlayerProfileData profile, string expectedAccount)
    {
        if (!HasUgsSession || AccountId != expectedAccount)
            throw new UgsAccountException("登录已失效，请重新登录。", true);
        ValidateUgsProfile(profile, expectedAccount);
        Profile = profile;
        ProfileChanged?.Invoke(Profile);
    }

    private void ValidateUgsProfile(PlayerProfileData profile, string expectedAccount)
    {
        if (profile == null || profile.accountId != expectedAccount || profile.uid != PublicUid || (!AccountUid.IsValid(profile.uid) && !(ugs.HasLegacyUid && profile.uid == 0)) || profile.revision < 0 ||
            !UgsCredentialRules.IsValidUsername(profile.username) || profile.avatarId >= 32 ||
            (profile.personal != null && !PersonalProfileData.IsValid(profile.personal)) ||
            !profile.appearance.Equals(profile.appearance.Sanitized()) || !profile.loadout.Equals(profile.loadout.Sanitized()) || profile.match != null)
            throw new UgsAccountException("云存档与当前账号不一致或格式异常，未应用数据。");
    }

    private bool ValidateUgsCredentials(string username, string password, bool registering, Action<bool, string> completed)
    {
        string error = !UgsCredentialRules.IsValidUsername(username?.Trim()) ? UgsCredentialRules.UsernameHelp :
            registering && !UgsCredentialRules.IsValidPassword(password) ? UgsCredentialRules.PasswordHelp :
            string.IsNullOrEmpty(password) || password.Length > 30 ? "请输入正确的密码（最多 30 位）。" : "";
        if (error == "") return true;
        LastError = error;
        StateChanged?.Invoke();
        completed?.Invoke(false, error);
        return false;
    }

    private void UgsSessionExpired()
    {
        if (this == null || ugsDisposed) return;
        ugsSessionGeneration++;
        ClearSession();
        LastError = "登录已失效，请重新登录。";
        StateChanged?.Invoke();
    }

    private void DisposeUgs()
    {
        ugsDisposed = true;
        ugsSessionGeneration++;
        if (ugs == null) return;
        ugs.SessionExpired -= UgsSessionExpired;
        try { ugs.Dispose(); } catch { /* OnDestroy must finish even when an external SDK is shutting down. */ }
        ugs = null;
    }

    private sealed class UgsResult
    {
        public string accountId, username;
        public PlayerProfileData profile;
    }
}
