using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>Account credentials and sessions remain in memory; the server owns the saved profile.</summary>
public sealed class AccountClient : MonoBehaviour
{
    public static AccountClient Instance { get; private set; }
    public bool IsAuthenticated => !string.IsNullOrEmpty(AccessToken) && Profile != null && DateTimeOffset.UtcNow.ToUnixTimeSeconds() < expiresAtUnixSeconds;
    public bool IsBusy { get; private set; }
    public string AccessToken { get; private set; } = "";
    public string LastError { get; private set; } = "";
    public PlayerProfileData Profile { get; private set; }
    public event Action StateChanged;
    public event Action<PlayerProfileData> ProfileChanged;
    private long expiresAtUnixSeconds;

    [Serializable] private class Credentials { public string username; public string password; }
    [Serializable] private class AuthResponse { public string accessToken; public long expiresAtUnixSeconds; public PlayerProfileData profile; }
    [Serializable] private class ErrorResponse { public string code; public string message; }
    [Serializable] private class SaveRequest { public int revision; public CharacterAppearanceData appearance; public PvpLoadoutData loadout; }
    [Serializable] private class TicketResponse { public string ticket; public long expiresAtUnixSeconds; }
    [Serializable] private class TicketRequest { public string ticket; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => Instance = null;

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
        DontDestroyOnLoad(gameObject);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        AccessToken = "";
        Profile = null;
    }

    public void Register(string username, string password, Action<bool, string> completed = null)
        => Authenticate("register", username, password, completed);

    public void Login(string username, string password, Action<bool, string> completed = null)
        => Authenticate("login", username, password, completed);

    private void Authenticate(string operation, string username, string password, Action<bool, string> completed)
    {
        if (!Begin(completed)) return;
        var payload = JsonUtility.ToJson(new Credentials { username = username, password = password });
        StartCoroutine(ClientRequest("POST", "/api/auth/" + operation, payload, "", (body, status, error) =>
        {
            if (error == "")
            {
                if (!TryParse(body, out AuthResponse response) || response.profile == null || string.IsNullOrEmpty(response.accessToken))
                    error = "账号服务返回的数据不完整。";
                else
                {
                    AccessToken = response.accessToken;
                    expiresAtUnixSeconds = response.expiresAtUnixSeconds;
                    Profile = response.profile;
                    ProfileChanged?.Invoke(Profile);
                }
            }
            Finish(error, completed);
        }));
    }

    public void RefreshProfile(Action<bool, string> completed = null)
    {
        if (!BeginAuthenticated(completed)) return;
        StartCoroutine(ClientRequest("GET", "/api/profile", null, AccessToken, (body, status, error) =>
        {
            ApplyProfileResponse(body, status, ref error);
            Finish(error, completed);
        }));
    }

    public void SaveProfile(CharacterAppearanceData appearance, PvpLoadoutData loadout, Action<bool, string> completed = null)
    {
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
    {
        if (!BeginAuthenticated((success, error) => { if (!success) completed?.Invoke(null, error); })) return;
        StartCoroutine(ClientRequest("POST", "/api/game-ticket", null, AccessToken, (body, status, error) =>
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

    public static IEnumerator ValidateServerSession(string token, Action<PlayerProfileData, string> completed)
        => ServerRequest("/api/auth/validate", null, token, completed);

    public static IEnumerator RedeemServerTicket(string ticket, Action<PlayerProfileData, string> completed)
        => ServerRequest("/api/game-ticket/redeem", JsonUtility.ToJson(new TicketRequest { ticket = ticket }), "", completed);

    private static IEnumerator ServerRequest(string route, string body, string token, Action<PlayerProfileData, string> completed)
    {
        if (!AccountServiceSettings.TryGetEndpoint(true, out var endpoint, out var timeout, out var configurationError))
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
            LastError = "请先登录账号。";
            StateChanged?.Invoke();
            completed?.Invoke(false, LastError);
            return false;
        }
        return Begin(completed);
    }

    private void ApplyProfileResponse(string body, long status, ref string error)
    {
        if (status == 401) ClearSession();
        if (error != "") return;
        if (!TryParse(body, out PlayerProfileData profile) || string.IsNullOrEmpty(profile.accountId))
        { error = "档案数据不完整，未应用更改。"; return; }
        Profile = profile;
        ProfileChanged?.Invoke(Profile);
    }

    private void ClearSession()
    {
        AccessToken = "";
        expiresAtUnixSeconds = 0;
        Profile = null;
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
