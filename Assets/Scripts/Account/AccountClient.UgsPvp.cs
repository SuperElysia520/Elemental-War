using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Unity.Services.CloudCode;
using Unity.Services.Core;

public sealed partial class AccountClient
{
    private const string UgsPvpUnavailable = "当前账号网关不支持 UGS PvP，请检查客户端版本。";

    private async Task<UgsPvpReply> CallUgsPvp(string operation, Dictionary<string, object> args)
    {
        if (!IsAuthenticated) throw new UgsAccountException("请先登录账号并载入用户数据。");
        var gateway = GetUgsGateway() as IUgsPvpGateway;
        if (gateway == null) throw new UgsAccountException(UgsPvpUnavailable);
        int generation = ugsSessionGeneration;
        string account = AccountId;
        UgsPvpReply reply;
        try { reply = await gateway.ExecutePvpAsync(operation, Username, args); }
        catch
        {
            RequireCurrentPvpRequest(generation, account);
            throw;
        }
        RequireCurrentPvpRequest(generation, account);
        if (!HasSession) throw new UgsAccountException("登录已失效，请重新登录。", true);
        return reply;
    }

    private void RequireCurrentPvpRequest(int generation, string account)
    {
        if (this == null || ugsDisposed || generation != ugsSessionGeneration || AccountId != account)
            throw new OperationCanceledException();
    }

    private async void RequestUgsGameTicket(bool matched, Action<string, string> completed)
    {
        if (!(GetUgsGateway() is IUgsPvpGateway)) { completed?.Invoke(null, UgsPvpUnavailable); return; }
        if (!BeginAuthenticated((ok, error) => { if (!ok) completed?.Invoke(null, error); })) return;
        string ticket = null, failure = "";
        string operation = matched ? "ticket" : "game-ticket";
        try
        {
            UgsPvpReply reply = await CallUgsPvp(operation, null);
            failure = PvpBusinessError(operation, reply);
            if (failure == "")
            {
                if (!TryParse(reply.body, out TicketResponse result) || result.ticket == null || result.ticket.Length != 64 ||
                    !IsHex(result.ticket) || result.expiresAtUnixSeconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                {
                    failure = "无法获取有效的入场凭证。";
                    LogUgsPvpFailure(operation, reply.status, UgsPvpFailureKind.InvalidResponse);
                }
                else ticket = result.ticket;
            }
        }
        catch (Exception error) { failure = HandleUgsPvpFailure(operation, error); }
        if (this == null || ugsDisposed) return;
        Finish(failure, null);
        completed?.Invoke(ticket, failure);
    }

    private IEnumerator UgsPartyRequest(string method, string route, string body, Action<string, long, string> completed)
    {
        if (!IsAuthenticated) { completed("", 401, "请先登录账号。"); yield break; }
        if (!(GetUgsGateway() is IUgsPvpGateway)) { completed("", 503, UgsPvpUnavailable); yield break; }
        string operation = method == "GET" && route == "/api/party" ? "current" :
            method == "GET" && route == "/api/party/rooms" ? "rooms" :
            method == "POST" && route != null && route.StartsWith("/api/party/", StringComparison.Ordinal) ? route.Substring(11) : null;
        if (operation != "current" && operation != "rooms" && operation != "create" && operation != "join" &&
            operation != "ready" && operation != "queue" && operation != "cancel" && operation != "leave" && operation != "fail")
        { completed("", 400, "不支持的队伍请求。"); yield break; }
        Dictionary<string, object> args = new Dictionary<string, object>();
        string parseError = null;
        try
        {
            if (!string.IsNullOrEmpty(body))
            {
                JObject json = JObject.Parse(body);
                foreach (var pair in json)
                {
                    if (operation == "join" && pair.Key == "partyId" && pair.Value.Type == JTokenType.String) args.Add(pair.Key, (string)pair.Value);
                    else if (operation == "ready" && pair.Key == "ready" && pair.Value.Type == JTokenType.Boolean) args.Add(pair.Key, (bool)pair.Value);
                    else { parseError = "队伍请求参数不正确。"; break; }
                }
            }
        }
        catch { parseError = "队伍请求参数不正确。"; }
        if (parseError != null) { completed("", 400, parseError); yield break; }
        Task<UgsPvpReply> task = CallUgsPvp(operation, args);
        while (!task.IsCompleted) yield return null;
        UgsPvpReply reply = null;
        string failure = "";
        try { reply = task.GetAwaiter().GetResult(); failure = PvpBusinessError(operation, reply); }
        catch (Exception error) { failure = HandleUgsPvpFailure(operation, error); }
        completed(reply?.body ?? "", reply?.status ?? 503, failure);
    }

    private string HandleUgsPvpFailure(string operation, Exception error)
    {
        UgsPvpFailureKind kind = ClassifyUgsPvpFailure(error, out int status);
        LogUgsPvpFailure(operation, status, kind);
        if (this != null && !ugsDisposed && UgsAccountGateway.IsSessionInvalid(error)) UgsSessionExpired();
        if (error is UgsPvpRequestException known) return known.Message;
        return UgsAccountGateway.FriendlyError(error);
    }

    private static string PvpBusinessError(string operation, UgsPvpReply reply)
    {
        if (reply == null || !reply.ok || reply.state != "done")
        {
            LogUgsPvpFailure(operation, reply?.status ?? 0, UgsPvpFailureKind.InvalidResponse);
            return "队伍返回数据不完整，请重试。";
        }
        if (reply.status >= 200 && reply.status < 300) return "";
        bool parsed = TryParse(reply.body, out ErrorResponse error);
        UgsPvpFailureKind kind = UgsPvpFailureKind.BusinessRejected;
        if (parsed && (error.code == "profile_corrupt" || error.code == "profile_unavailable")) kind = UgsPvpFailureKind.ProfileUnavailable;
        else if (parsed && error.code == "cloud_unavailable") kind = UgsPvpFailureKind.CloudUnavailable;
        LogUgsPvpFailure(operation, reply.status, kind);
        return parsed && !string.IsNullOrEmpty(error.message) ? error.message : "队伍操作失败，请重试。";
    }

    private static UgsPvpFailureKind ClassifyUgsPvpFailure(Exception error, out int status)
    {
        // Zero means no HTTP/bridge status was supplied, rather than HTTP 0.
        status = 0;
        if (error is UgsPvpRequestException known) { status = known.Status; return known.Kind; }
        if (error is OperationCanceledException) return UgsPvpFailureKind.Cancelled;
        if (UgsAccountGateway.IsSessionInvalid(error)) return UgsPvpFailureKind.SessionInvalid;
        if (error is CloudCodeException code)
        {
            if (code.Reason == CloudCodeExceptionReason.NotFound && code.ErrorCode == 9002)
            { status = 404; return UgsPvpFailureKind.ScriptMissing; }
            switch (code.Reason)
            {
                case CloudCodeExceptionReason.NoInternetConnection: return UgsPvpFailureKind.TransportError;
                case CloudCodeExceptionReason.TooManyRequests: return UgsPvpFailureKind.RateLimited;
                case CloudCodeExceptionReason.ScriptError: return UgsPvpFailureKind.ScriptError;
                case CloudCodeExceptionReason.InvalidArgument: return UgsPvpFailureKind.InvalidRequest;
                case CloudCodeExceptionReason.ServiceUnavailable: return UgsPvpFailureKind.ServiceError;
            }
        }
        if (error is RequestFailedException request)
        {
            if (request.ErrorCode == CommonErrorCodes.TransportError) return UgsPvpFailureKind.TransportError;
            if (request.ErrorCode == CommonErrorCodes.Timeout) return UgsPvpFailureKind.ResponseTimeout;
            if (request.ErrorCode == CommonErrorCodes.TooManyRequests || request.ErrorCode == 429) return UgsPvpFailureKind.RateLimited;
        }
        return UgsPvpFailureKind.Unknown;
    }

    private static void LogUgsPvpFailure(string operation, int status, UgsPvpFailureKind kind)
    {
        // Background polls update their UI status without repeating a warning every
        // ten seconds. Only these fixed user operations may reach the log.
        switch (operation)
        {
            case "create": case "join": case "ready": case "queue": case "cancel":
            case "leave": case "fail": case "ticket": case "game-ticket": break;
            default: return;
        }
        if (!Enum.IsDefined(typeof(UgsPvpFailureKind), kind)) kind = UgsPvpFailureKind.Unknown;
        int safeStatus = status >= 100 && status <= 599 ? status : 0;
        UnityEngine.Debug.LogWarning("[UGS PvP] Request failed; operation=" + operation +
            "; status=" + safeStatus + "; category=" + kind);
    }

    private static bool IsHex(string value)
    {
        foreach (char c in value) if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    private static bool TryGetPvpServerEndpoint(out string endpoint, out int timeout, out string error)
    {
        if (!AccountServiceSettings.TryGetEndpoint(true, out endpoint, out timeout, out error)) return false;
        if (AccountServiceSettings.SelectedBackend == AccountBackend.UnityGamingServices &&
            (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri uri) || !uri.IsLoopback))
        { error = "UGS 专服验证必须连接本机雨云桥接服务。"; return false; }
        return true;
    }
}
