using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>Project-local UGS setup. It never requests or stores service-account credentials.</summary>
public sealed class UgsBackendSetupWindow : EditorWindow
{
    private AccountServiceSettings settings;
    private TextField environmentName, profileScript;
    private Label result;
    private const string ScriptPath = "Server/UgsCloudCode/FpsProfile.js";
    private const string GuidePath = "docs/AI/UgsBackendSetup.md";
    private const string Parameters = "operation | String | Required\nusername | String | Optional\nrevision | Numeric | Optional\nappearance | JSON | Optional\nloadout | JSON | Optional\navatarId | Numeric | Optional";

    [MenuItem("Tools/FPS/UGS Backend/Configure")]
    public static void Open() => GetWindow<UgsBackendSetupWindow>("UGS 后端配置");

    public void CreateGUI()
    {
        rootVisualElement.Clear();
        rootVisualElement.style.paddingLeft = rootVisualElement.style.paddingRight = 12;
        rootVisualElement.style.paddingTop = rootVisualElement.style.paddingBottom = 12;
        settings = Resources.Load<AccountServiceSettings>("AccountServiceSettings");
        minSize = new Vector2(480, 510);
        var content = new ScrollView();
        rootVisualElement.Add(content);
        content.Add(new HelpBox("先在已有 Unity Cloud 项目启用 Username & Password、Cloud Save，并发布 FpsProfile 云脚本。下面保存本地配置，云端开通按指南完成。", HelpBoxMessageType.Info));
        content.Add(new TextField("已关联的项目 ID") { value = Application.cloudProjectId, isReadOnly = true });
        content.Add(new Label("当前后端：" + (settings != null ? settings.backend.ToString() : "缺少配置")));
        content.Add(new Button(() => Application.OpenURL("https://cloud.unity.com/")) { text = "打开 Unity Cloud 控制台" });
        content.Add(new Button(() => SettingsService.OpenProjectSettings("Project/Services")) { text = "打开 Project Settings > Services" });
        environmentName = new TextField("环境名称") { value = settings != null ? settings.ugsEnvironment : "development" };
        profileScript = new TextField("Cloud Code 脚本名") { value = settings != null ? settings.ugsProfileScript : "FpsProfile" };
        content.Add(environmentName);
        content.Add(profileScript);
        content.Add(new Button(SaveConfiguration) { text = "保存 UGS 设置" });
        content.Add(new Button(CopyScript) { text = "复制 FpsProfile 云脚本" });
        content.Add(new Button(() =>
        {
            EditorGUIUtility.systemCopyBuffer = Parameters;
            result.text = "已复制参数表。控制台需手工添加这 6 个参数；可选参数不要设置默认值。";
        }) { text = "复制云脚本参数表" });
        content.Add(new Button(() => EditorUtility.OpenWithDefaultApp(Path.GetFullPath(GuidePath))) { text = "打开逐步配置指南" });
        content.Add(new Button(() => result.text = CheckConfiguration()) { text = "检查本地配置" });
        content.Add(new Button(ReadLiveProfile) { text = "读取真实 UGS 存档（需已登录）" });
        content.Add(new Button(() =>
        {
            string path = Path.GetFullPath("Server/UgsCloudCode/FpsPvp.js");
            if (!File.Exists(path)) { result.text = "缺少 FpsPvp.js，请检查工程文件。"; return; }
            EditorGUIUtility.systemCopyBuffer = File.ReadAllText(path);
            result.text = "已复制 FpsPvp。请在相同环境创建 JS 脚本，配置参数后保存并发布。";
        }) { text = "复制 FpsPvp 匹配云脚本" });
        content.Add(new Button(() =>
        {
            EditorGUIUtility.systemCopyBuffer = "action | String | Required\nrequestId | String | Required\noperation | String | Optional\nusername | String | Optional\nargs | JSON | Optional";
            result.text = "已复制 PvP 参数表；仅 action、requestId 必填，可选参数不设默认值。";
        }) { text = "复制 PvP 云脚本参数表" });
        content.Add(new Button(() => EditorUtility.OpenWithDefaultApp(Path.GetFullPath("docs/AI/UgsPvpRainyunSetup.md")))
            { text = "打开 UGS PvP 与雨云部署指南" });
        content.Add(new HelpBox("匹配需要发布 FpsPvp 并启动雨云桥接服务。服务账号密钥只在雨云端配置，不能填进 Unity 工程。客户端与专服都需更新为协议 5。", HelpBoxMessageType.Info));
        result = new Label();
        result.style.whiteSpace = WhiteSpace.Normal;
        result.style.marginTop = 12;
        content.Add(result);
    }

    private void SaveConfiguration()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) { result.text = "请先停止运行，再保存配置。"; return; }
        if (settings == null) { result.text = "缺少 Resources/AccountServiceSettings。"; return; }
        if (string.IsNullOrWhiteSpace(environmentName.value) || string.IsNullOrWhiteSpace(profileScript.value))
        { result.text = "请填写控制台环境名称和已发布脚本名。"; return; }
        Undo.RecordObject(settings, "Configure UGS Account Backend");
        settings.backend = AccountBackend.UnityGamingServices;
        settings.ugsEnvironment = environmentName.value.Trim();
        settings.ugsProfileScript = profileScript.value.Trim();
        EditorUtility.SetDirty(settings);
        AssetDatabase.SaveAssetIfDirty(settings);
        result.text = CheckConfiguration();
    }

    private void CopyScript()
    {
        if (!File.Exists(ScriptPath)) { result.text = "未找到 " + ScriptPath; return; }
        EditorGUIUtility.systemCopyBuffer = File.ReadAllText(ScriptPath);
        result.text = "已复制。请粘贴到控制台的 FpsProfile JavaScript 脚本，添加参数后 Save 并 Publish。";
    }

    private void ReadLiveProfile()
    {
        var account = AccountClient.Instance;
        if (!EditorApplication.isPlaying || account == null || !account.UsesUgs || !account.HasSession)
        { result.text = "请先运行 Login 场景并登录 UGS 账号。"; return; }
        if (account.IsBusy) { result.text = "请等待当前账号请求完成。"; return; }
        result.text = "正在读取云存档…";
        account.RefreshProfile((ok, error) =>
        {
            if (this != null && result != null) result.text = ok ? "读取成功。请修改外观并保存，再重新登录检查。" : error;
        });
    }

    public static string CheckConfiguration()
    {
        if (AccountServiceSettings.SelectedBackend != AccountBackend.UnityGamingServices)
            return "当前未选择 UnityGamingServices 后端，请保存 UGS 设置，并检查 FPS_ACCOUNT_BACKEND 环境变量。";
        if (!AccountServiceSettings.TryGetUgsConfiguration(out string environment, out string script, out string error)) return error;
        return "本地配置完整。环境：" + environment + "；脚本：" + script + "。控制台开通、脚本发布和真实网络连接需要按指南验证。";
    }

    public static void WriteConfigurationReport()
    {
        Directory.CreateDirectory("tmp/UgsIntegration");
        File.WriteAllText("tmp/UgsIntegration/local-configuration.txt", CheckConfiguration());
    }
}
