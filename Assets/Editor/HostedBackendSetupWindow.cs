using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

/// <summary>Configure the public game endpoint without editing scenes or exposing server configuration.</summary>
public sealed class HostedBackendSetupWindow : EditorWindow
{
    [Serializable] private sealed class HealthResponse { public string status, service; public int apiVersion; }
    private TextField address;
    private Label result;
    private Button checkButton;
    private UnityWebRequest request;
    private UnityWebRequestAsyncOperation operation;

    [MenuItem("Tools/FPS/Cloud Backend/Configure")]
    public static void Open() => GetWindow<HostedBackendSetupWindow>("云后端配置");

    public void CreateGUI()
    {
        rootVisualElement.Clear();
        rootVisualElement.style.paddingLeft = rootVisualElement.style.paddingRight = 12;
        rootVisualElement.style.paddingTop = rootVisualElement.style.paddingBottom = 12;
        var instructions = new Label("在雨云服务器上完成安装后，把安装结果中的 HTTPS 地址粘贴到这里，再保存并检查连接。");
        instructions.style.whiteSpace = WhiteSpace.Normal;
        rootVisualElement.Add(instructions);
        var settings = Resources.Load<AccountServiceSettings>("AccountServiceSettings");
        address = new TextField("服务器 HTTPS 地址") { value = settings != null ? settings.publicBaseUrl : "" };
        address.style.marginTop = 12;
        rootVisualElement.Add(address);
        rootVisualElement.Add(new Button(Save) { text = "保存地址" });
        checkButton = new Button(CheckConnection) { text = "检查连接" };
        rootVisualElement.Add(checkButton);
        rootVisualElement.Add(new Button(() => EditorUtility.OpenWithDefaultApp(
            Path.GetFullPath(Path.Combine(Application.dataPath, "../docs/AI/RainyunBackendDeployment.md")))) { text = "打开部署说明" });
        result = new Label("这里只保存客户端公开地址。雨云上的游戏专服继续通过本机回环地址访问后端。");
        result.style.whiteSpace = WhiteSpace.Normal;
        result.style.marginTop = 12;
        rootVisualElement.Add(result);
        minSize = new Vector2(480, 240);
    }

    private bool TryReadAddress(out string endpoint)
    {
        endpoint = "";
        if (!Uri.TryCreate(address.value.Trim(), UriKind.Absolute, out Uri uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "")
        {
            result.text = "请粘贴安装结果中的完整 HTTPS 地址，例如 https://你的服务器公网IP 。";
            return false;
        }
        endpoint = uri.AbsoluteUri.TrimEnd('/');
        return true;
    }

    private void Save()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) { result.text = "请先停止运行，再保存配置。"; return; }
        if (!TryReadAddress(out string endpoint)) return;
        var settings = Resources.Load<AccountServiceSettings>("AccountServiceSettings");
        if (settings == null) { result.text = "缺少 Resources/AccountServiceSettings 配置。"; return; }
        Undo.RecordObject(settings, "Configure hosted backend");
        settings.publicBaseUrl = endpoint;
        EditorUtility.SetDirty(settings);
        AssetDatabase.SaveAssetIfDirty(settings);
        result.text = "已保存。可以检查连接，然后在 Login 场景测试注册和登录。已有进程环境变量会优先于这里的地址。";
    }

    private void CheckConnection()
    {
        if (!TryReadAddress(out string endpoint)) return;
        CancelRequest();
        request = UnityWebRequest.Get(endpoint + "/health");
        request.timeout = 15;
        request.redirectLimit = 0;
        try { operation = request.SendWebRequest(); }
        catch (Exception) { CancelRequest(); result.text = "连接未能发起，请检查 HTTPS 地址。"; return; }
        checkButton.SetEnabled(false);
        result.text = "正在连接云后端……";
        EditorApplication.update += Poll;
    }

    private void Poll()
    {
        if (operation == null || !operation.isDone) return;
        bool success = false;
        if (request.result == UnityWebRequest.Result.Success)
        {
            try
            {
                var health = JsonUtility.FromJson<HealthResponse>(request.downloadHandler.text);
                success = health != null && health.status == "ok" && health.service == "fps-account" && health.apiVersion >= 2;
            }
            catch (ArgumentException) { }
        }
        result.text = success ? "连接成功，云后端版本正确。请保存地址后测试注册和登录。" :
            "未连接到新版账号后端。请确认雨云安装成功、公网 443 端口可访问，且地址没有包含 /health 或 /api。";
        CancelRequest();
    }

    private void OnDisable() => CancelRequest();
    private void CancelRequest()
    {
        EditorApplication.update -= Poll;
        if (request != null) { if (operation != null && !operation.isDone) request.Abort(); request.Dispose(); }
        operation = null;
        request = null;
        checkButton?.SetEnabled(true);
    }
}
