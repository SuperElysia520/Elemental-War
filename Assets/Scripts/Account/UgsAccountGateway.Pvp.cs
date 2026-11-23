using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using UnityEngine.Networking;
using Unity.Services.CloudCode;

public interface IUgsPvpGateway
{
    Task<UgsPvpReply> ExecutePvpAsync(string operation, string username, Dictionary<string, object> args);
}

[Serializable]
public sealed class UgsPvpReply
{
    public bool ok;
    public string code, state, requestId, body;
    public int status;
}

internal enum UgsPvpFailureKind
{
    Unknown, ScriptMissing, BridgeUnavailable, BridgeRestarted, RequestBusy,
    RequestSuperseded, RequestExpired, ResponseTimeout, AuthenticationRequired,
    InvalidRequest, InvalidResponse, SessionInvalid, GatewayUnavailable, Cancelled,
    BusinessRejected, ProfileUnavailable, CloudUnavailable, TransportError,
    RateLimited, ScriptError, ServiceError
}

// Only fixed local messages and categories belong here. Never retain the SDK
// exception, request, player identity, or mailbox body in diagnostic metadata.
internal sealed class UgsPvpRequestException : Exception
{
    internal readonly UgsPvpFailureKind Kind;
    internal readonly int Status;
    internal UgsPvpRequestException(UgsPvpFailureKind kind, int status, string message) : base(message)
    {
        Kind = kind;
        Status = status >= 100 && status <= 599 ? status : 0;
    }
}

public sealed partial class UgsAccountGateway
{
    public const string PvpScript = "FpsPvp";
    private readonly SemaphoreSlim pvpGate = new SemaphoreSlim(1, 1);
    private sealed class Grant { public string url, certificateSha256, token; public long expiresAt; }
    private sealed class Bootstrap { public Grant directLobby; }
    private Grant lobbyGrant;
    private string lobbyPlayer;
    private int probeGeneration;
    private long nextProbe;
    private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public bool HasDirectLobby => lobbyGrant != null && lobbyGrant.expiresAt > NowMs + 5000 && lobbyPlayer == PlayerId && IsSignedIn;

    private sealed class PinnedLobbyCertificate : CertificateHandler
    {
        private readonly string expected;
        public PinnedLobbyCertificate(string expected) { this.expected = expected; }
        protected override bool ValidateCertificate(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return false;
            using (var hash = SHA256.Create())
                return string.Equals(BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", ""), expected, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void ResetDirectLobby()
    {
        probeGeneration++;
        lobbyGrant = null;
        lobbyPlayer = null;
        nextProbe = 0;
    }

    private static async Task SendDirectRequest(UnityWebRequest request, Grant grant)
    {
        request.redirectLimit = 0;
        request.certificateHandler = new PinnedLobbyCertificate(grant.certificateSha256);
        var operation = request.SendWebRequest();
        // Unity resumes the continuation on the player loop.
        while (!operation.isDone) await Task.Yield();
    }

    private async Task ProbeDirectLobbyAsync(string player, string body)
    {
        if (NowMs < nextProbe || HasDirectLobby) return;
        int generation = ++probeGeneration;
        nextProbe = NowMs + 30000;
        try
        {
            var grant = JsonConvert.DeserializeObject<Bootstrap>(body)?.directLobby;
            if (grant == null || grant.expiresAt <= NowMs + 5000 || !Hex64(grant.certificateSha256) || !Hex64(grant.token) ||
                !Uri.TryCreate(grant.url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.AbsolutePath != "/lobby/v1" ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return;
            using (var probe = UnityWebRequest.Get(grant.url + "/ping"))
            {
                probe.timeout = 3;
                await SendDirectRequest(probe, grant);
                if (probe.result != UnityWebRequest.Result.Success || probe.downloadHandler.text != "fps-lobby-v1") return;
            }
            if (generation == probeGeneration && IsSignedIn && PlayerId == player)
            {
                lobbyGrant = grant;
                lobbyPlayer = player;
            }
        }
        catch (Exception) { /* Probe failure leaves existing mailbox access available. */ }
    }

    private static bool Hex64(string value) => value != null && value.Length == 64 && Array.TrueForAll(value.ToCharArray(), Uri.IsHexDigit);

    private async Task<UgsPvpReply> CallDirectLobbyAsync(string player, string operation, Dictionary<string, object> args)
    {
        var grant = lobbyGrant;
        string id = Guid.NewGuid().ToString("N");
        using (var request = new UnityWebRequest(grant.url + "/command", "POST"))
        {
            request.timeout = 10;
            string json = JsonConvert.SerializeObject(new { requestId = id, operation, args = args ?? new Dictionary<string, object>() });
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + grant.token);
            await SendDirectRequest(request, grant);
            RequireSamePlayer(player);
            if (request.result == UnityWebRequest.Result.ConnectionError || request.result == UnityWebRequest.Result.DataProcessingError ||
                request.responseCode == 401 || request.responseCode < 200 || request.responseCode > 599)
            {
                if (ReferenceEquals(lobbyGrant, grant)) ResetDirectLobby();
                throw new UgsPvpRequestException(UgsPvpFailureKind.TransportError, (int)request.responseCode, "大厅直连中断，请刷新状态后重试。");
            }
            return new UgsPvpReply { ok = true, state = "done", requestId = id, status = (int)request.responseCode, body = request.downloadHandler.text };
        }
    }

    public async Task<UgsPvpReply> ExecutePvpAsync(string operation, string username, Dictionary<string, object> args)
    {
        RequireSession();
        string player = PlayerId;
        if (HasDirectLobby) return await CallDirectLobbyAsync(player, operation, args);
        await pvpGate.WaitAsync();
        try
        {
            RequireSamePlayer(player);
            if (HasDirectLobby) return await CallDirectLobbyAsync(player, operation, args);
            string requestId = Guid.NewGuid().ToString("N");
            var parameters = new Dictionary<string, object>
            {
                { "action", "submit" }, { "requestId", requestId }, { "operation", operation },
                { "username", username }, { "args", args ?? new Dictionary<string, object>() }
            };
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                UgsPvpReply reply;
                try { reply = await CloudCodeService.Instance.CallEndpointAsync<UgsPvpReply>(PvpScript, parameters); }
                catch (CloudCodeException error) when (error.Reason == CloudCodeExceptionReason.NotFound && error.ErrorCode == 9002)
                { throw new UgsPvpRequestException(UgsPvpFailureKind.ScriptMissing, 404, "找不到 PvP 云脚本 FpsPvp，请在当前环境发布。"); }
                RequireSamePlayer(player);
                if (reply == null) throw new UgsPvpRequestException(UgsPvpFailureKind.InvalidResponse, 0, "PvP 服务返回为空，请检查云脚本。");
                if (!reply.ok) throw new UgsPvpRequestException(PvpFailureKind(reply.code), reply.status, PvpError(reply.code));
                if (reply.requestId != requestId) throw new UgsPvpRequestException(UgsPvpFailureKind.RequestSuperseded, 0, "PvP 请求已被其他设备替换，请重试。");
                if (reply.state == "done")
                {
                    if (reply.status < 200 || reply.status > 599 || reply.body == null || reply.body.Length > 65536)
                        throw new UgsPvpRequestException(UgsPvpFailureKind.InvalidResponse, reply.status, "PvP 返回数据不完整，请重试。");
                    if (operation == "current" && reply.status == 200) _ = ProbeDirectLobbyAsync(player, reply.body);
                    return reply;
                }
                if (reply.state != "pending") throw new UgsPvpRequestException(UgsPvpFailureKind.InvalidResponse, reply.status, "PvP 请求状态无效，请重试。");
                if (elapsed.Elapsed.TotalSeconds >= 35) throw new UgsPvpRequestException(UgsPvpFailureKind.ResponseTimeout, 0, "匹配服务响应超时，请检查雨云桥接服务。");
                await Task.Delay(450);
                RequireSamePlayer(player);
                parameters = new Dictionary<string, object> { { "action", "status" }, { "requestId", requestId } };
            }
        }
        finally { pvpGate.Release(); }
    }

    private static UgsPvpFailureKind PvpFailureKind(string code)
    {
        switch (code)
        {
            case "bridge_unavailable": return UgsPvpFailureKind.BridgeUnavailable;
            case "bridge_restarted": return UgsPvpFailureKind.BridgeRestarted;
            case "request_busy": return UgsPvpFailureKind.RequestBusy;
            case "request_superseded": return UgsPvpFailureKind.RequestSuperseded;
            case "request_expired": return UgsPvpFailureKind.RequestExpired;
            case "auth_required": return UgsPvpFailureKind.AuthenticationRequired;
            case "invalid_request": return UgsPvpFailureKind.InvalidRequest;
            default: return UgsPvpFailureKind.Unknown;
        }
    }

    private static string PvpError(string code)
    {
        switch (code)
        {
            case "bridge_unavailable": return "雨云匹配桥接服务未就绪，请稍后重试。";
            case "bridge_restarted": return "匹配服务已重启，请重新创建队伍。";
            case "request_busy": return "此账号有另一项队伍请求正在处理中，请稍后重试。";
            case "request_superseded": return "队伍请求已被其他设备替换，请重试。";
            case "request_expired": return "队伍请求超时，请检查雨云桥接服务后重试。";
            case "auth_required": return "请先登录账号。";
            case "invalid_request": return "队伍请求参数不正确。";
            default: return "无法连接 PvP 服务，请检查云脚本和雨云桥接配置。";
        }
    }
}
