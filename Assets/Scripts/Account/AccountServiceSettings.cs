using System;
using UnityEngine;

[CreateAssetMenu(menuName = "FPS/Account Service Settings", fileName = "AccountServiceSettings")]
public sealed class AccountServiceSettings : ScriptableObject
{
    [Tooltip("客户端注册、登录与云存档 HTTPS 地址。未部署前留空；本机开发可使用 http://127.0.0.1:5080。")]
    public string publicBaseUrl = "";

    [Tooltip("专用服务器访问账号服务的地址。同机部署可使用回环 HTTP，仅兑换入场凭证。")]
    public string serverBaseUrl = "http://127.0.0.1:5080";

    [Range(3, 30)] public int timeoutSeconds = 10;

    public static bool TryGetEndpoint(bool forServer, out string endpoint, out int timeout, out string error)
    {
        endpoint = "";
        timeout = 10;
        error = "";
        var settings = Resources.Load<AccountServiceSettings>("AccountServiceSettings");
        if (settings == null)
        {
            error = "缺少账号服务配置，请创建 Resources/AccountServiceSettings。";
            return false;
        }
        string configured = forServer ? settings.serverBaseUrl : settings.publicBaseUrl;
        // Per-process endpoints support deployment and two-client local tests without changing production assets.
        var environment = Environment.GetEnvironmentVariable(forServer ? "FPS_ACCOUNT_SERVER_URL" : "FPS_ACCOUNT_CLIENT_URL");
        if (!string.IsNullOrWhiteSpace(environment)) configured = environment;
        if (string.IsNullOrWhiteSpace(configured))
        {
            error = "账号云服务尚未配置，暂时无法登录或保存角色。";
            return false;
        }
        if (!Uri.TryCreate(configured.Trim(), UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            error = "账号服务地址必须使用 HTTPS；仅本机回环地址允许 HTTP。";
            return false;
        }
        endpoint = uri.AbsoluteUri.TrimEnd('/');
        timeout = Mathf.Clamp(settings.timeoutSeconds, 3, 30);
        return true;
    }
}
