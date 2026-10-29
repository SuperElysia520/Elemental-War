using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

/// <summary>Wardrobe and loadout for the logged-in account. Authentication belongs to the Login scene.</summary>
public sealed class CharacterCustomizationUI : MonoBehaviour
{
    private static CharacterCustomizationUI current;
    private AccountClient account;
    private LobbyManager boundLobby;
    private CharacterAppearanceData appearance;
    private PvpLoadoutData loadout;
    private Action onClose;
    private Font font;
    private Text status, accountLabel;
    private Text returnButtonLabel;
    private string returnLabel = "返回大厅";
    private Button saveButton, reloadButton;
    private bool cloudActionsWereLocked;
    private GameObject previewRoot;
    private RifleGirlAppearance preview;
    private RenderTexture previewTexture;
    private RawImage previewImage;
    private bool dirty, discardOnNextClose, reloadOnNextClick;
    private readonly System.Collections.Generic.List<Button> choices = new System.Collections.Generic.List<Button>();
    private readonly System.Collections.Generic.List<ChoiceBinding> bindings = new System.Collections.Generic.List<ChoiceBinding>();
    private readonly string[] outfits = { "外套裙装", "裙装", "运动装" };
    private readonly string[] sights = { "铁瞄", "红点 1", "红点 2", "红点 3", "全息瞄具", "8 倍镜" };

    public static void Show(Action closed = null, string returnButtonText = "返回大厅")
    {
        if (current != null)
        {
            current.onClose = closed;
            current.returnLabel = returnButtonText;
            if (current.returnButtonLabel != null) current.returnButtonLabel.text = returnButtonText;
            current.gameObject.SetActive(true);
            return;
        }
        var go = new GameObject("CharacterCustomization", typeof(RectTransform));
        current = go.AddComponent<CharacterCustomizationUI>();
        current.onClose = closed;
        current.returnLabel = returnButtonText;
        current.Build();
    }

    private void Build()
    {
        account = AccountClient.EnsureInstance();
        account.StateChanged += RefreshStatus;
        account.ProfileChanged += ReceiveProfile;
        boundLobby = LobbyManager.Instance;
        if (boundLobby != null) boundLobby.OnLobbyChanged += RefreshStatus;
        appearance = account.Profile != null ? account.Profile.appearance : CharacterAppearanceData.Default;
        loadout = account.Profile != null ? account.Profile.loadout : PvpLoadoutData.Default;
        font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "Noto Sans CJK SC", "Arial" }, 22);
        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 150;
        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1600, 900);
        scaler.matchWidthOrHeight = 0.5f;
        gameObject.AddComponent<GraphicRaycaster>();
        Panel(transform, "Background", new Vector2(0, 0), new Vector2(1, 1), new Color(0.035f, 0.05f, 0.085f, 0.99f));
        Label(transform, "RIFLEGIRL / 角色与配装", 42, 40, 1050, 56, 30, new Color(0.88f, 0.94f, 1f));
        returnButtonLabel = Button(transform, returnLabel, 1360, 40, 196, 48, Close).GetComponentInChildren<Text>();

        var left = Panel(transform, "Options", new Vector2(0, 0), new Vector2(0, 0), new Color(0.075f, 0.1f, 0.15f));
        Place(left.rectTransform, 42, 118, 550, 682);
        Label(left.transform, "外观", 24, 18, 480, 40, 25, Color.white);
        Choice(left.transform, "服装", 68, () => outfits[appearance.outfitId], step => { appearance.outfitId = Cycle(appearance.outfitId, step, 3); Changed(); });
        Choice(left.transform, "面部预设", 126, () => "预设 " + (appearance.faceId + 1), step => { appearance.faceId = Cycle(appearance.faceId, step, 5); Changed(); });
        Choice(left.transform, "衣装配色", 184, () => appearance.outfitId == 2 ? "运动装原色" : "配色 " + (appearance.clothColorId + 1), step => { if (appearance.outfitId != 2) appearance.clothColorId = Cycle(appearance.clothColorId, step, 5); Changed(); });
        Choice(left.transform, "发色", 242, () => "发色 " + (appearance.hairColorId + 1), step => { appearance.hairColorId = Cycle(appearance.hairColorId, step, 4); Changed(); });
        Choice(left.transform, "帽子", 300, () => appearance.hat ? "佩戴" : "不佩戴", step => { appearance.hat = !appearance.hat; Changed(); });
        Choice(left.transform, "战斗配件", 358, () => appearance.outfitId == 2 ? "此套装不适用" : (appearance.accessories ? "佩戴" : "不佩戴"), step => { if (appearance.outfitId != 2) appearance.accessories = !appearance.accessories; Changed(); });
        Label(left.transform, "PVP 配装", 24, 426, 480, 38, 25, Color.white);
        Choice(left.transform, "主武器", 472, () => loadout.primaryWeaponId == 2 ? "狙击枪" : "突击步枪", step => { loadout.primaryWeaponId = (byte)(loadout.primaryWeaponId == 0 ? 2 : 0); Changed(); });
        Choice(left.transform, "主武器瞄具", 530, () => sights[loadout.primarySightId], step => { loadout.primarySightId = Cycle(loadout.primarySightId, step, 6); Changed(); });
        Choice(left.transform, "手枪瞄具", 588, () => sights[loadout.sidearmSightId], step => { loadout.sidearmSightId = (byte)(loadout.sidearmSightId == 0 ? 3 : 0); Changed(); });

        var imageObject = new GameObject("CharacterPreview", typeof(RectTransform), typeof(RawImage));
        imageObject.transform.SetParent(transform, false);
        previewImage = imageObject.GetComponent<RawImage>();
        Place(previewImage.rectTransform, 624, 118, 466, 682);
        previewImage.color = Color.white;
        var rotate = imageObject.AddComponent<EventTrigger>();
        var drag = new EventTrigger.Entry { eventID = EventTriggerType.Drag };
        drag.callback.AddListener(data => { if (preview != null) preview.transform.Rotate(Vector3.up, -((PointerEventData)data).delta.x * 0.35f, Space.World); });
        rotate.triggers.Add(drag);
        Label(transform, "拖动旋转角色", 660, 754, 380, 30, 18, new Color(0.6f, 0.7f, 0.82f));

        var right = Panel(transform, "Account", Vector2.zero, Vector2.zero, new Color(0.075f, 0.1f, 0.15f));
        Place(right.rectTransform, 1122, 118, 434, 682);
        Label(right.transform, "账号与云端档案", 24, 20, 384, 44, 25, Color.white);
        accountLabel = Label(right.transform, "", 24, 74, 380, 64, 20, new Color(0.62f, 0.8f, 0.94f));
        reloadButton = Button(right.transform, "重新载入云端档案", 24, 155, 378, 42, ReloadProfile);
        Label(right.transform, "注册、登录及切换账号请在 Login 界面进行。\n保存后，开局和重生使用此配装。", 24, 221, 378, 90, 17, new Color(0.62f, 0.7f, 0.81f));
        saveButton = Button(right.transform, "保存到账号", 24, 565, 378, 62, Save);
        status = Label(transform, "", 44, 823, 1278, 52, 20, new Color(0.64f, 0.82f, 0.98f));
        CreatePreview();
        RefreshStatus();
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    private void CreatePreview()
    {
        var prefab = Resources.Load<GameObject>("Customization/RifleGirlBody");
        if (prefab == null) { status.text = "角色资源尚未准备完成。"; return; }
        previewRoot = new GameObject("WardrobePreviewStage");
        previewRoot.transform.position = new Vector3(10000, -10000, 10000);
        var body = Instantiate(prefab, previewRoot.transform);
        body.transform.localPosition = Vector3.zero;
        body.transform.localRotation = Quaternion.Euler(0, 180, 0);
        SetLayer(body.transform, 31);
        preview = body.GetComponentInChildren<RifleGirlAppearance>();
        if (preview != null) preview.Apply(appearance);
        Animator animator = body.GetComponent<Animator>();
        animator.runtimeAnimatorController = Resources.Load<RuntimeAnimatorController>("Customization/RifleGirlLobby");
        animator.applyRootMotion = false;
        animator.Rebind();
        animator.Update(0f);
        var cam = new GameObject("PreviewCamera", typeof(Camera)).GetComponent<Camera>();
        cam.transform.SetParent(previewRoot.transform, false);
        cam.fieldOfView = 35f;
        cam.cullingMask = 1 << 31;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.055f, 0.078f, 0.12f);
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 10f;
        previewTexture = new RenderTexture(700, 1024, 24) { antiAliasing = 2 };
        cam.targetTexture = previewTexture;
        previewImage.texture = previewTexture;
        // Frame the visible, animated outfit rather than a fixed bind-pose height.
        Bounds bounds = new Bounds(body.transform.position + Vector3.up * 0.85f, new Vector3(1f, 1.7f, 0.5f));
        bool hasGeometry = false;
        var baked = new Mesh();
        foreach (SkinnedMeshRenderer skin in body.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            if (!skin.enabled) continue;
            baked.Clear();
            skin.BakeMesh(baked);
            Bounds local = baked.bounds;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = local.center + Vector3.Scale(local.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector3 point = skin.transform.TransformPoint(corner);
                if (!hasGeometry) { bounds = new Bounds(point, Vector3.zero); hasGeometry = true; }
                else bounds.Encapsulate(point);
            }
        }
        Destroy(baked);
        float halfHeight = Mathf.Max(bounds.extents.y, bounds.extents.x / (700f / 1024f));
        float distance = halfHeight * 1.15f / Mathf.Tan(cam.fieldOfView * Mathf.Deg2Rad * 0.5f) + bounds.extents.z;
        cam.transform.position = bounds.center + Vector3.back * distance;
        cam.transform.LookAt(bounds.center);
        var light = new GameObject("PreviewKey", typeof(Light)).GetComponent<Light>();
        light.transform.SetParent(previewRoot.transform, false);
        light.type = LightType.Directional;
        light.transform.localRotation = Quaternion.Euler(35, -30, 0);
        light.cullingMask = 1 << 31;
        light.intensity = 1.4f;
    }
    private static void SetLayer(Transform root, int layer) { root.gameObject.layer = layer; foreach (Transform child in root) SetLayer(child, layer); }
    private void ReceiveProfile(PlayerProfileData profile)
    {
        if (profile == null) return;
        appearance = profile.appearance.Sanitized();
        loadout = profile.loadout.Sanitized();
        dirty = discardOnNextClose = reloadOnNextClick = false;
        if (preview != null) preview.Apply(appearance);
        RefreshStatus();
    }
    private void Changed()
    {
        discardOnNextClose = reloadOnNextClick = false;
        appearance = appearance.Sanitized();
        loadout = loadout.Sanitized();
        dirty = true;
        if (preview != null) preview.Apply(appearance);
        status.text = "有未保存的更改。";
        RefreshStatus();
    }
    private void Save()
    {
        if (!AllowCloudAction()) return;
        if (!account.IsAuthenticated) { status.text = "请返回 Login 界面登录账号。"; return; }
        account.SaveProfile(appearance, loadout, (ok, message) => { if (this != null) status.text = ok ? "外观与配装已保存到云端。" : message; });
    }
    private void ReloadProfile()
    {
        if (!AllowCloudAction()) return;
        if (!account.IsAuthenticated || account.IsBusy) return;
        if (dirty && !reloadOnNextClick)
        {
            status.text = "重新载入会放弃未保存的更改。再次点击确认载入云端档案。";
            reloadOnNextClick = true;
            return;
        }
        account.RefreshProfile((ok, message) => { if (this != null) status.text = ok ? "已载入最新云端档案。" : message; });
    }
    private void RefreshStatus()
    {
        if (accountLabel == null) return;
        accountLabel.text = account.IsAuthenticated ? "已登录：" + account.Profile.username + "\nUID: " + account.Profile.accountId : "请返回 Login 界面登录账号。";
        bool cloudAllowed = CanModifyCloudProfile(out string lockMessage);
        if (!cloudAllowed) status.text = lockMessage;
        else if (!string.IsNullOrEmpty(account.LastError)) status.text = account.LastError;
        else if (cloudActionsWereLocked)
            status.text = dirty ? "开局检查已结束，可继续保存更改或返回放弃。" : "开局检查已结束，可继续编辑角色与配装。";
        cloudActionsWereLocked = !cloudAllowed;
        bool canRequest = cloudAllowed && !account.IsBusy;
        saveButton.interactable = account.IsAuthenticated && canRequest;
        reloadButton.interactable = account.IsAuthenticated && canRequest;
        saveButton.GetComponentInChildren<Text>().text = !cloudAllowed ? "开局期间暂不可保存" : account.IsBusy ? "正在同步…" : (dirty ? "保存更改到账号" : "保存到账号");
        foreach (var choice in choices) choice.interactable = !account.IsBusy;
        foreach (var binding in bindings) binding.label.text = binding.value();
    }
    private bool AllowCloudAction()
    {
        if (CanModifyCloudProfile(out string message)) return true;
        status.text = message;
        return false;
    }
    private static bool CanModifyCloudProfile(out string message)
    {
        LobbyState state = LobbyManager.Instance != null ? LobbyManager.Instance.CurrentLobby : null;
        if (state != null && state.GameMode == LobbyGameMode.PVP)
        {
            if (state.PvpIsPreparingMatch.Value)
            {
                message = "正在确认全员开局配装，暂时不能保存或切换账号。未保存搭配仍可试穿或返回放弃。";
                return false;
            }
            if (state.IsStarted.Value)
            {
                message = "对局正在开始，请在返回大厅后保存角色或切换账号。";
                return false;
            }
        }
        message = "";
        return true;
    }
    private void Close()
    {
        if (account.IsBusy) { status.text = "正在同步，请稍候。"; return; }
        if (dirty && !discardOnNextClose)
        {
            status.text = "尚有未保存更改。保存到账号，或再次点击返回放弃。";
            discardOnNextClose = true;
            return;
        }
        var callback = onClose;
        Destroy(gameObject);
        callback?.Invoke();
    }
    private void OnDestroy()
    {
        if (account != null) { account.StateChanged -= RefreshStatus; account.ProfileChanged -= ReceiveProfile; }
        if (boundLobby != null) boundLobby.OnLobbyChanged -= RefreshStatus;
        if (previewRoot != null) Destroy(previewRoot);
        if (previewTexture != null) { previewTexture.Release(); Destroy(previewTexture); }
        if (font != null) Destroy(font);
        if (current == this) current = null;
    }
    private static byte Cycle(byte value, int step, int count) => (byte)((value + step + count) % count);
    private void Choice(Transform parent, string title, float y, Func<string> value, Action<int> change)
    {
        Label(parent, title, 24, y, 155, 42, 20, new Color(0.67f, 0.75f, 0.86f));
        var display = Label(parent, value(), 212, y, 224, 42, 20, Color.white);
        display.alignment = TextAnchor.MiddleCenter;
        bindings.Add(new ChoiceBinding { value = value, label = display });
        choices.Add(Button(parent, "‹", 171, y, 40, 42, () => { if (!account.IsBusy) change(-1); }));
        choices.Add(Button(parent, "›", 455, y, 40, 42, () => { if (!account.IsBusy) change(1); }));
    }
    private Image Panel(Transform parent, string name, Vector2 min, Vector2 max, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image)); go.transform.SetParent(parent, false);
        var rect = go.GetComponent<RectTransform>(); rect.anchorMin = min; rect.anchorMax = max; rect.offsetMin = rect.offsetMax = Vector2.zero;
        var image = go.GetComponent<Image>(); image.color = color; return image;
    }
    private Text Label(Transform parent, string text, float x, float y, float w, float h, int size, Color color)
    {
        var go = new GameObject("Text", typeof(RectTransform), typeof(Text)); go.transform.SetParent(parent, false);
        var label = go.GetComponent<Text>(); label.font = font; label.fontSize = size; label.text = text; label.color = color;
        label.alignment = TextAnchor.MiddleLeft; label.raycastTarget = false;
        Place(label.rectTransform, x, y, w, h); return label;
    }
    private Button Button(Transform parent, string text, float x, float y, float w, float h, Action click)
    {
        var panel = Panel(parent, text, Vector2.zero, Vector2.zero, new Color(0.14f, 0.24f, 0.34f));
        Place(panel.rectTransform, x, y, w, h);
        var button = panel.gameObject.AddComponent<Button>(); button.targetGraphic = panel;
        button.onClick.AddListener(() => click());
        var label = Label(panel.transform, text, 0, 0, w, h, 20, Color.white); label.alignment = TextAnchor.MiddleCenter;
        return button;
    }
    private static void Place(RectTransform rect, float x, float y, float w, float h)
    {
        rect.anchorMin = rect.anchorMax = new Vector2(0, 1); rect.pivot = new Vector2(0, 1);
        rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(w, h);
    }
    private sealed class ChoiceBinding { public Func<string> value; public Text label; }
}
