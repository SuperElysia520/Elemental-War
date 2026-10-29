using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>Passwords are never persisted by the game; the selected backend owns player profiles.</summary>
public sealed partial class AccountClient : MonoBehaviour
{
    public static AccountClient Instance { get; private set; }
    public bool UsesUgs { get; private set; }
    public bool SupportsRegistrationLookup => !UsesUgs;
    public bool HasSession => UsesUgs ? HasUgsSession : !string.IsNullOrEmpty(AccessToken) && DateTimeOffset.UtcNow.ToUnixTimeSeconds() < expiresAtUnixSeconds;
    public bool IsAuthenticated => HasSession && Profile != null;
    public string AccountId { get; private set; } = "";
    public string Username { get; private set; } = "";
    public bool IsBusy { get; private set; }
    private string legacyAccessToken = "";
    public string AccessToken { get => UsesUgs ? UgsAccessToken : legacyAccessToken; private set => legacyAccessToken = value; }
    public string LastError { get; private set; } = "";
    public PlayerProfileData Profile { get; private set; }
    public event Action StateChanged;
    public event Action<PlayerProfileData> ProfileChanged;
    private long expiresAtUnixSeconds;

    [Serializable] private class Credentials { public string username; public string password; }
    [Serializable] private class RegistrationCheckRequest { public string username; }
    [Serializable] private class RegistrationCheckResponse { public bool registered; }
    [Serializable] private class RegistrationResponse { public string username; }
    [Serializable] private class AuthResponse { public string accessToken; public long expiresAtUnixSeconds; public PlayerProfileData profile; }
    [Serializable] private class ErrorResponse { public string code; public string message; }
    [Serializable] private class SaveRequest { public int revision; public CharacterAppearanceData appearance; public PvpLoadoutData loadout; }
    [Serializable] private class TicketResponse { public string ticket; public long expiresAtUnixSeconds; }
    [Serializable] private class TicketRequest { public string ticket; }
    [Serializable] private class AvatarRequest { public byte avatarId; }
    [Serializable] private class Reservation { public string matchId; }
    [Serializable] private class MatchEvent { public string matchId; public int serverPort; public string @event; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() { Instance = null; UgsGatewayFactory = null; }

    public static AccountClient EnsureInstance()
    {
        if (Instance == null)
        {
            Instance = FindObjectOfType<AccountClient>();
            if (Instance == null) Instance = new GameObject("AccountClient").AddComponent<AccountClient>();
        }
        return Instance;
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        UsesUgs = AccountServiceSettings.SelectedBackend == AccountBackend.UnityGamingServices;
        DontDestroyOnLoad(gameObject);
    }

    private void OnDestroy()
    {
        DisposeUgs();
        if (Instance == this) Instance = null;
        AccessToken = "";
        Profile = null;
    }

    public void Register(string username, string password, Action<bool, string> completed = null)
    {
        if (UsesUgs) RegisterUgs(username, password, completed);
        else Authenticate("register", username, password, completed);
    }

    public void Login(string username, string password, Action<bool, string> completed = null)
        => Authenticate("login", username, password, completed);

    /// <summary>Authenticate in Login; defer applying the profile until GameStart requests it.</summary>
    public void LoginSession(string username, string password, Action<bool, string> completed = null)
        => Authenticate("login", username, password, completed, false);

    public void CheckRegistration(string username, Action<bool, bool, string> completed)
    {
        if (UsesUgs)
        { completed?.Invoke(false, false, "UGS 不提供公开的注册状态查询，请直接登录或点击注册。"); return; }
        if (!Begin((ok, error) => { if (!ok) completed?.Invoke(false, false, error); })) return;
        StartCoroutine(ClientRequest("POST", "/api/auth/check-registration",
            JsonUtility.ToJson(new RegistrationCheckRequest { username = username }), "", (body, status, error) =>
        {
            bool registered = false;
            if (error == "")
            {
                if (!TryParse(body, out RegistrationCheckResponse response) || !body.Contains("\"registered\""))
                    error = "账号服务返回的注册状态不完整。";
                else registered = response.registered;
            }
            Finish(error, null);
            completed?.Invoke(error == "", registered, error);
        }));
    }

    /// <summary>Registration alone never creates a client session or applies user data.</summary>
    public void RegisterAccount(string username, string password, Action<bool, string> completed = null)
    {
        if (UsesUgs) { RegisterUgs(username, password, completed); return; }
        if (!Begin(completed)) return;
        StartCoroutine(ClientRequest("POST", "/api/auth/register-account",
            JsonUtility.ToJson(new Credentials { username = username, password = password }), "", (body, status, error) =>
        {
            if (error == "" && (!TryParse(body, out RegistrationResponse response) || string.IsNullOrEmpty(response.username)))
                error = "账号服务返回的注册结果不完整。";
            Finish(error, completed);
        }));
    }

    private void Authenticate(string operation, string username, string password, Action<bool, string> completed, bool applyProfile = true)
    {
        if (UsesUgs) { AuthenticateUgs(operation, username, password, completed, applyProfile); return; }
        if (!Begin(completed)) return;
        var payload = JsonUtility.ToJson(new Credentials { username = username, password = password });
        StartCoroutine(ClientRequest("POST", "/api/auth/" + operation, payload, "", (body, status, error) =>
        {
            if (error == "")
            {
                if (!TryParse(body, out AuthResponse response) || response.profile == null || string.IsNullOrEmpty(response.profile.accountId)
                    || string.IsNullOrEmpty(response.profile.username) || string.IsNullOrEmpty(response.accessToken)
                    || response.expiresAtUnixSeconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                    error = "账号服务返回的数据不完整。";
                else
                {
                    AccessToken = response.accessToken;
                    expiresAtUnixSeconds = response.expiresAtUnixSeconds;
                    AccountId = response.profile.accountId;
                    Username = response.profile.username;
                    Profile = applyProfile ? response.profile : null;
                    ProfileChanged?.Invoke(Profile);
                }
            }
            Finish(error, completed);
        }));
    }

    public void RefreshProfile(Action<bool, string> completed = null)
    {
        if (UsesUgs) { LoadUgsProfile(completed); return; }
        if (!HasSession) { RejectMissingSession(completed); return; }
        if (!Begin(completed)) return;
        StartCoroutine(ClientRequest("GET", "/api/profile", null, AccessToken, (body, status, error) =>
        {
            ApplyProfileResponse(body, status, ref error);
            Finish(error, completed);
        }));
    }

    public void SaveProfile(CharacterAppearanceData appearance, PvpLoadoutData loadout, Action<bool, string> completed = null)
    {
        if (UsesUgs) { SaveUgsProfile(appearance, loadout, completed); return; }
        if (!BeginAuthenticated(completed)) return;
        var payload = JsonUtility.ToJson(new SaveRequest { revision = Profile.revision, appearance = appearance.Sanitized(), loadout = loadout.Sanitized() });
        StartCoroutine(ClientRequest("PUT", "/api/profile", payload, AccessToken, (body, status, error) =>
        {
            ApplyProfileResponse(body, status, ref error);
            Finish(error, completed);
        }));
    }

    public void Logout(Action<bool, string> completed = null)
    {
        if (UsesUgs) { LogoutUgs(completed); return; }
        if (!Begin(completed)) return;
        var token = AccessToken;
        ClearSession();
        if (string.IsNullOrEmpty(token)) { Finish("", completed); return; }
        StartCoroutine(ClientRequest("POST", "/api/auth/logout", null, token, (body, status, error) =>
        {
            // A revoked/expired session is already logged out. Network failures remain visible.
            Finish(status == 401 ? "" : error, completed);
        }));
    }

    public void RequestGameTicket(Action<string, string> completed)
        => RequestGameTicketAt("/api/game-ticket", completed);

    public void RequestMatchedGameTicket(Action<string, string> completed)
        => RequestGameTicketAt("/api/party/ticket", completed);

    private void RequestGameTicketAt(string route, Action<string, string> completed)
    {
        if (UsesUgs) { RequestUgsGameTicket(route == "/api/party/ticket", completed); return; }
        if (!BeginAuthenticated((success, error) => { if (!success) completed?.Invoke(null, error); })) return;
        StartCoroutine(ClientRequest("POST", route, null, AccessToken, (body, status, error) =>
        {
            string ticket = null;
            if (error == "")
            {
                if (!TryParse(body, out TicketResponse response) || string.IsNullOrEmpty(response.ticket)) error = "无法获取入场凭证。";
                else ticket = response.ticket;
            }
            if (status == 401) ClearSession();
            Finish(error, null);
            completed?.Invoke(ticket, error);
        }));
    }

    public void SaveAvatar(byte avatarId, Action<bool, string> completed = null)
    {
        if (UsesUgs) { SaveUgsAvatar(avatarId, completed); return; }
        if (!BeginAuthenticated(completed)) return;
        StartCoroutine(ClientRequest("PUT", "/api/profile/avatar", JsonUtility.ToJson(new AvatarRequest { avatarId = avatarId }), AccessToken,
            (body, status, error) => { ApplyProfileResponse(body, status, ref error); Finish(error, completed); }));
    }

    /// <summary>Party polling uses the same validated endpoint and session without marking profile editing busy.</summary>
    public IEnumerator PartyRequest(string method, string route, string body, Action<string, long, string> completed)
    {
        if (UsesUgs) { yield return UgsPartyRequest(method, route, body, completed); yield break; }
        if (!IsAuthenticated) { completed("", 401, "请先登录账号。"); yield break; }
        yield return ClientRequest(method, route, body, AccessToken, (response, status, error) =>
        { if (status == 401) ClearSession(); completed(response, status, error); });
    }

    public static IEnumerator ReadSlotReservation(int port, Action<string, string> completed)
    {
        bool ugsServer = AccountServiceSettings.SelectedBackend == AccountBackend.UnityGamingServices;
        if (!TryGetPvpServerEndpoint(out var endpoint, out var timeout, out var configurationError))
        { completed(null, configurationError); yield break; }
        yield return Send(endpoint, timeout, "GET", (ugsServer ? "/api/ugs" : "/api") + "/matches/server/" + port, null, "", (body, status, error) =>
        {
            Reservation reservation = null;
            if (error == "" && !TryParse(body, out reservation)) error = "无法确认服务器预约状态。";
            completed(error == "" ? reservation.matchId : null, error);
        });
    }

    public static IEnumerator SendMatchServerEvent(string matchId, int port, string eventName, Action<string> completed)
    {
        bool ugsServer = AccountServiceSettings.SelectedBackend == AccountBackend.UnityGamingServices;
        if (!TryGetPvpServerEndpoint(out var endpoint, out var timeout, out var configurationError))
        { completed(configurationError); yield break; }
        yield return Send(endpoint, timeout, "POST", (ugsServer ? "/api/ugs" : "/api") + "/matches/server",
            JsonUtility.ToJson(new MatchEvent { matchId = matchId, serverPort = port, @event = eventName }), "",
            (body, status, error) => completed(error));
    }

    public static IEnumerator ValidateServerSession(string token, Action<PlayerProfileData, string> completed)
        => ServerRequest("/api/auth/validate", null, token, completed);

    public static IEnumerator RedeemServerTicket(string ticket, Action<PlayerProfileData, string> completed)
        => ServerRequest("/api/game-ticket/redeem", JsonUtility.ToJson(new TicketRequest { ticket = ticket }), "", completed);

    private static IEnumerator ServerRequest(string route, string body, string token, Action<PlayerProfileData, string> completed)
    {
        if (AccountServiceSettings.SelectedBackend == AccountBackend.UnityGamingServices)
        {
            // UGS long-lived player JWTs are never accepted on the game transport or sent to legacy APIs.
            if (route != "/api/game-ticket/redeem" || !string.IsNullOrEmpty(token))
            { completed?.Invoke(null, "UGS 专服只接受一次性入场凭证。"); yield break; }
            route = "/api/ugs/game-ticket/redeem";
        }
        if (!TryGetPvpServerEndpoint(out var endpoint, out var timeout, out var configurationError))
        { completed?.Invoke(null, configurationError); yield break; }
        yield return Send(endpoint, timeout, "POST", route, body, token, (response, status, error) =>
        {
            PlayerProfileData profile = null;
            if (error == "" && (!TryParse(response, out profile) || string.IsNullOrEmpty(profile.accountId)))
                error = "账号验证返回的数据不完整。";
            completed?.Invoke(error == "" ? profile : null, error);
        });
    }

    private IEnumerator ClientRequest(string method, string route, string body, string token, Action<string, long, string> completed)
    {
        if (!AccountServiceSettings.TryGetEndpoint(false, out var endpoint, out var timeout, out var error))
        { completed("", 0, error); yield break; }
        yield return Send(endpoint, timeout, method, route, body, token, completed);
    }

    private static IEnumerator Send(string endpoint, int timeout, string method, string route, string body, string token, Action<string, long, string> completed)
    {
        using (var request = new UnityWebRequest(endpoint + route, method))
        {
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = timeout;
            request.redirectLimit = 0;
            request.SetRequestHeader("Accept", "application/json");
            if (!string.IsNullOrEmpty(token)) request.SetRequestHeader("Authorization", "Bearer " + token);
            if (body != null)
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                request.SetRequestHeader("Content-Type", "application/json");
            }
            UnityWebRequestAsyncOperation operation = null;
            string startupError = null;
            try { operation = request.SendWebRequest(); }
            catch (InvalidOperationException)
            {
                startupError = "无法发起账号请求，请检查服务地址、HTTPS 证书和开发环境网络设置。";
            }
            if (startupError != null) { completed("", 0, startupError); yield break; }
            yield return operation;
            string error = "";
            string response = request.downloadHandler.text;
            if (request.result != UnityWebRequest.Result.Success)
            {
                if (TryParse(response, out ErrorResponse result) && !string.IsNullOrEmpty(result.message)) error = result.message;
                else error = request.responseCode == 0 ? "无法连接账号服务，请检查网络或服务地址。" : "账号请求失败（" + request.responseCode + "）。";
            }
            completed(response, request.responseCode, error);
        }
    }

    private bool Begin(Action<bool, string> completed)
    {
        if (IsBusy) { completed?.Invoke(false, "账号请求正在处理中，请稍候。"); return false; }
        IsBusy = true;
        LastError = "";
        StateChanged?.Invoke();
        return true;
    }

    private bool BeginAuthenticated(Action<bool, string> completed)
    {
        if (!IsAuthenticated)
        {
            RejectMissingSession(completed);
            return false;
        }
        return Begin(completed);
    }

    private void RejectMissingSession(Action<bool, string> completed)
    {
        LastError = "请先登录账号并载入用户数据。";
        StateChanged?.Invoke();
        completed?.Invoke(false, LastError);
    }

    private void ApplyProfileResponse(string body, long status, ref string error)
    {
        if (status == 401) ClearSession();
        if (error != "") return;
        if (!TryParse(body, out PlayerProfileData profile) || string.IsNullOrEmpty(profile.accountId))
        { error = "档案数据不完整，未应用更改。"; return; }
        if (profile.accountId != AccountId)
        { error = "档案与当前登录账号不一致，未应用更改。"; return; }
        Profile = profile;
        ProfileChanged?.Invoke(Profile);
    }

    private void ClearSession()
    {
        AccessToken = "";
        expiresAtUnixSeconds = 0;
        Profile = null;
        AccountId = Username = "";
        ProfileChanged?.Invoke(null);
    }

    private void Finish(string error, Action<bool, string> completed)
    {
        IsBusy = false;
        LastError = error;
        StateChanged?.Invoke();
        completed?.Invoke(string.IsNullOrEmpty(error), error);
    }

    private static bool TryParse<T>(string json, out T value) where T : class
    {
        value = null;
        if (string.IsNullOrEmpty(json)) return false;
        try { value = JsonUtility.FromJson<T>(json); return value != null; }
        catch (ArgumentException) { return false; }
    }
}
