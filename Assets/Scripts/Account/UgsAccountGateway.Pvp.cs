using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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

    public async Task<UgsPvpReply> ExecutePvpAsync(string operation, string username, Dictionary<string, object> args)
    {
        RequireSession();
        string player = PlayerId;
        await pvpGate.WaitAsync();
        try
        {
            RequireSamePlayer(player);
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
