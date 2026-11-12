using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FPS.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Real scenes, prefabs, account client and buttons with an isolated in-memory account/PvP boundary.</summary>
[InitializeOnLoad]
public static class LobbyMvcValidation
{
    private const string Prefix = "FPS.LobbyMvcValidation.";
    private const string Output = "tmp/LobbyMvc/runtime-validation.json";
    private static readonly FieldInfo Factory = typeof(AccountClient).GetField("UgsGatewayFactory", BindingFlags.Static | BindingFlags.NonPublic);
    [Serializable] private sealed class Report
    {
        public bool success, restored;
        public string scope = "Actual Login/GameLobby scenes, prefab references, button actions, layout and lifecycle; fake UGS boundary; no online services.";
        public List<string> checks = new List<string>(), errors = new List<string>(), baselineErrors = new List<string>();
    }
    [Serializable] private sealed class Layout { public List<string> ui; }
    private static Report report;
    private static FakeGateway fake;
    private static object originalFactory;
    private static readonly Stack<IEnumerator> steps = new Stack<IEnumerator>();
    private static bool Running => SessionState.GetBool(Prefix + "running", false);
    private static string Stage { get => SessionState.GetString(Prefix + "stage", ""); set => SessionState.SetString(Prefix + "stage", value); }
    static LobbyMvcValidation()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += state =>
        { if (Running && state == PlayModeStateChange.EnteredPlayMode && Stage == "starting") Stage = "entered"; };
        Application.logMessageReceived += Log;
        if (Running && Stage == "testing") EditorApplication.delayCall += () => Finish("Scripts reloaded during runtime verification.");
    }
    [MenuItem("Tools/FPS/Lobby/Validate MVC Runtime (Offline)")]
    public static void Run()
    {
        if (Running || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
            throw new InvalidOperationException("Wait until the Editor has stopped and compiled.");
        var scene = SceneManager.GetActiveScene();
        if ((scene.path != "Assets/Scenes/GameLobby.unity" && scene.path != "Assets/Scenes/Login.unity") || SceneManager.sceneCount != 1)
            throw new InvalidOperationException("Open Login or GameLobby before validation.");
        SessionState.SetString(Prefix + "originalScene", scene.path);
        report = new Report();
        if (scene.name == "Login")
        {
            var lobby = EditorSceneManager.OpenScene("Assets/Scenes/GameLobby.unity", OpenSceneMode.Additive);
            try { ValidateAssets(false); }
            finally { EditorSceneManager.CloseScene(lobby, true); }
        }
        else ValidateAssets(false);
        Store();
        SessionState.SetString(Prefix + "environment", Environment.GetEnvironmentVariable("FPS_ACCOUNT_BACKEND") ?? "");
        SessionState.SetBool(Prefix + "hadEnvironment", Environment.GetEnvironmentVariable("FPS_ACCOUNT_BACKEND") != null);
        SessionState.SetBool(Prefix + "background", Application.runInBackground);
        SessionState.SetString(Prefix + "started", DateTime.UtcNow.Ticks.ToString());
        SessionState.SetBool(Prefix + "running", true); Stage = "starting";
        Environment.SetEnvironmentVariable("FPS_ACCOUNT_BACKEND", "UnityGamingServices");
        SessionState.SetString(Prefix + "previousPlayStart", AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene));
        SessionState.SetBool(Prefix + "originalDirty", scene.isDirty);
        EditorSceneManager.playModeStartScene = AssetDatabase.LoadAssetAtPath<SceneAsset>("Assets/Scenes/Login.unity");
        EditorApplication.isPaused = false; EditorApplication.isPlaying = true;
    }
    public static void Audit()
    {
        report = new Report(); ValidateAssets(); report.success = report.errors.Count == 0;
        File.WriteAllText("tmp/LobbyMvc/asset-validation.json", JsonUtility.ToJson(report, true));
    }

    public static void Cancel()
    {
        if (Running) Finish("Validation cancelled before restart.");
    }

    private static void ValidateAssets(bool compareMigrationLayout = true)
    {
        var root = UnityEngine.Object.FindObjectOfType<LobbyUIRoot>(true);
        Check(root != null && root.pages.Length == 7 && root.pages.Select(page => page.page).Distinct().Count() == 7, "Seven unique scene panel bindings");
        Check(root.pages.All(page => PrefabUtility.GetCorrespondingObjectFromSource(page) != null), "Every page is a prefab instance");
        Check(root.messageDialog != null && root.confirmDialog != null, "Modal layers are bound");
        Check(!root.gameObject.scene.GetRootGameObjects().SelectMany(item => item.GetComponentsInChildren<Transform>(true)).Any(item => item.name == "LoadingOverlay"), "GameLobby has no LoadingOverlay instance");
        Check(root.partyCharacters.Length == 5 && root.partyCharacters.All(item => item != null), "Five scene character previews remain bound");
        Check(root.roomsView.entryPrefab.mapName != null && root.roomsView.entryPrefab.roomState != null && root.roomsView.entryPrefab.avatars.All(avatar => avatar != null), "Room cards bind map, state, join and five avatar fields");
        uint[] missingCharacters;
        Check(root.partyView.status.font.HasCharacters("正在创建队伍等待成员准备团队对抗", out missingCharacters, true, true), "Lobby status fonts cover required Chinese characters");
        var clients = GameFlowBuildScenes.Get(false); var servers = GameFlowBuildScenes.Get(true);
        Check(clients[0].EndsWith("Login.unity") && clients.Count(path => path.EndsWith("GameLobby.unity")) == 1 && !clients.Any(path => path.EndsWith("GameStart.unity")), "Client build uses Login, one GameLobby and no GameStart");
        Check(servers[0].EndsWith("GameLobby.unity") && !servers.Any(path => path.EndsWith("Login.unity")), "Dedicated-server build starts in GameLobby");
        foreach (string path in Directory.GetFiles("Assets/Resource/UI/Prefab/Mvc", "*.prefab"))
        {
            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                foreach (var transform in contents.GetComponentsInChildren<Transform>(true))
                {
                    if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject) != 0)
                        throw new InvalidOperationException("Missing script: " + path + "/" + transform.name);
                    foreach (var component in transform.GetComponents<Component>())
                    {
                        if (component == null) continue;
                        var serialized = new SerializedObject(component); var property = serialized.GetIterator();
                        while (property.NextVisible(true))
                        {
                            if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                            var value = property.objectReferenceValue;
                            var target = value is GameObject go ? go.transform : value is Component reference ? reference.transform : null;
                            if (target != null && !EditorUtility.IsPersistent(target) && !target.IsChildOf(contents.transform))
                                throw new InvalidOperationException(path + " has external scene reference: " + property.propertyPath);
                        }
                    }
                }
                Check(true, "Valid scripts and local references: " + Path.GetFileName(path));
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }
        Check(true, "Panel prefabs have no external scene-object references");
        // The migration audit can compare its historical snapshot. Runtime validation
        // accepts later authored layout edits and checks current prefab wiring instead.
        if (compareMigrationLayout)
        {
            var layout = JsonUtility.FromJson<Layout>(File.ReadAllText("tmp/LobbyMvc/baseline.json"));
            var prefixes = new Dictionary<LobbyPage, string> { { LobbyPage.MainMenu, "StartMenuCanvas/MainMenu" }, { LobbyPage.Home, "OverlayCanvas/HomePanel" },
                { LobbyPage.Party, "OverlayCanvas/LobbyPanel" }, { LobbyPage.Rooms, "OverlayCanvas/RoomPanel" }, { LobbyPage.CustomRoom, "StartMenuCanvas/LobbyMenu" }, { LobbyPage.Profile, "OverlayCanvas/PersonalHomepage" } };
            int compared = 0;
            foreach (var page in root.pages)
            {
                if (!prefixes.TryGetValue(page.page, out string prefix)) continue;
                foreach (string line in layout.ui)
                {
                    string[] values = line.Split('|'); if (!values[0].StartsWith(prefix + "/", StringComparison.Ordinal)) continue;
                    // TMP creates and rebuilds these transient rendering children; they are not authored layout.
                    if (values[0].Contains("/TMP SubMeshUI [") || values[0].EndsWith(" Input Caret", StringComparison.Ordinal)) continue;
                    var rect = page.transform.Find(values[0].Substring(prefix.Length + 1)) as RectTransform;
                    if (rect == null) throw new InvalidOperationException("Authored UI was removed: " + values[0]);
                    if (rect.anchorMin.ToString() != values[1] || rect.anchorMax.ToString() != values[2] || rect.anchoredPosition.ToString() != values[3] || rect.sizeDelta.ToString() != values[4])
                        throw new InvalidOperationException("Authored layout changed: " + values[0]);
                    compared++;
                }
            }
            Check(compared > 150, "Preserved " + compared + " authored child RectTransform layouts");
        }
        Check(root.pages.All(page => page.GetComponent<MainMenuUI>() == null && page.GetComponent<LobbyMenuUI>() == null), "New panels do not use legacy singleton menu components");
        Check(root.GetComponent<MultiplayerLobbyUI>() == null && root.GetComponent<PartyLobbyUI>() == null, "Only one lobby UI composition root");
    }
    private static void Tick()
    {
        if (!Running) return;
        if (Stage == "stopping" && !EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling)
        { Restore(); return; }
        if (long.TryParse(SessionState.GetString(Prefix + "started", "0"), out long started) && DateTime.UtcNow.Ticks - started > TimeSpan.FromSeconds(180).Ticks)
        { Finish("Runtime verification timed out."); return; }
        if (Stage == "entered" && EditorApplication.isPlaying && !EditorApplication.isCompiling)
        {
            Stage = "testing"; originalFactory = Factory.GetValue(null); fake = new FakeGateway();
            Factory.SetValue(null, (Func<IUgsAccountGateway>)(() => fake));
            Application.runInBackground = true;
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>()) if (window.GetType().Name == "GameView") { window.Focus(); break; }
            steps.Clear(); steps.Push(Probe());
        }
        if (Stage != "testing" || steps.Count == 0) return;
        try
        {
            var current = steps.Peek();
            if (!current.MoveNext()) { steps.Pop(); if (steps.Count == 0) Finish(null); }
            else if (current.Current is IEnumerator nested) steps.Push(nested);
        }
        catch (Exception exception) { Finish(exception.Message); }
    }
    private static IEnumerator Probe()
    {
        foreach (var valid in new[] { "名字测试一二", "Abc123Xyz789", "名字测试一AB", "旅行者123456", "𠮷𠮷𠮷𠮷𠮷𠮷", "A", "名" })
            Check(DisplayNameRules.IsValid(valid), "Accepts a valid weighted name: " + valid);
        foreach (var invalid in new[] { "", "名字测试一二三", "Abc123Xyz7890", "名字测试一ABC", "名字测试一二A", "a b", "name_1", "🙂", "\uD840" })
            Check(!DisplayNameRules.IsValid(invalid), "Rejects an invalid/overweight name: " + invalid);
        Check(DisplayNameRules.TryGetLength("名字测试一AB", out int mixedLength) && mixedLength == 12, "Five Han characters and two ASCII characters have weighted length 12");
        Check(PersonalProfileData.IsValid(new PersonalProfileData { displayName = "LegacyLongName123" }), "Old cloud names remain readable without being silently renamed");
        yield return Wait(() => UnityEngine.Object.FindObjectOfType<LoginSceneController>() != null, "Login scene initialization");
        var flow = UnityEngine.Object.FindObjectOfType<LoginSceneController>();
        flow.enterGameAfterLogin = false;
        flow.Login("mvc_validation", "Validation9!Pass");
        yield return Wait(() => !flow.IsBusy, "Login completion");
        var account = AccountClient.Instance;
        Check(account.HasSession && account.Profile == null, "Login establishes a session without loading a profile");
        var ui = flow.GetComponent<LoginUI>();
        var loadingStages = new HashSet<string>();
        bool progressCompletedInLogin = false;
        flow.Changed += () =>
        {
            if (flow.ViewState != LoginViewState.Loading) return;
            loadingStages.Add(ui.loadingStatusText != null ? ui.loadingStatusText.text : "");
            if (flow.LoadingProgress == 1f)
                progressCompletedInLogin = SceneManager.GetActiveScene().name == "Login" &&
                    ui.loadingProgressImage.fillAmount == 1f && ui.loadingProgressTmpText != null && ui.loadingProgressTmpText.text == "100%";
        };
        var failedGate = fake.LoadGate;
        flow.EnterGame();
        yield return Wait(() => fake.LoadCalls == 1, "Profile loading starts inside Login");
        Check(flow.ViewState == LoginViewState.Loading && ui.loadingPanel.activeInHierarchy && ui.loadingStatusText != null && ui.loadingStatusText.text == "正在读取用户数据…", "Login displays its authored LoadingPanel and Status while reading data");
        Check(SceneManager.GetActiveScene().name == "Login" && SceneManager.sceneCount == 1 && account.Profile == null && flow.LoadingProgress == 0f, "GameLobby is not loaded before the profile response");
        flow.EnterGame(); flow.SwitchAccount(); flow.ShowLogin();
        yield return null; yield return null;
        Check(fake.LoadCalls == 1 && flow.IsBusy && flow.ViewState == LoginViewState.Loading, "Pending profile request blocks duplicate entry and account switching");
        yield return Capture("login-profile-loading");
        failedGate.SetException(new UgsAccountException("测试网络不可用，请重试。"));
        yield return Wait(() => !flow.IsBusy, "Recoverable profile failure");
        Check(SceneManager.GetActiveScene().name == "Login" && flow.ViewState == LoginViewState.Ready && account.HasSession && !account.IsAuthenticated && !ui.loadingPanel.activeSelf && flow.Message.Contains("测试网络不可用"), "Profile failure stays in Login with a retryable session and reason");
        var retryGate = new TaskCompletionSource<PlayerProfileData>(); fake.LoadGate = retryGate;
        flow.EnterGame();
        yield return Wait(() => fake.LoadCalls == 2, "Retry reads the profile again");
        retryGate.SetResult(Clone(fake.Profile));
        yield return Wait(() => flow.ViewState == LoginViewState.InitialName && !flow.IsBusy, "Initial name prompt after first profile load");
        Check(SceneManager.GetActiveScene().name == "Login" && ui.initialNamePanel != null && ui.initialNamePanel.gameObject.activeInHierarchy && !ui.loadingPanel.activeSelf && account.Profile.personal == null,
            "First authenticated entry without a cloud display name opens the authored InitialNamePanel before loading GameLobby");
        var initial = ui.initialNamePanel;
        var initialHint = initial.transform.Find("bc/NameBc/Status").GetComponent<UnityEngine.UI.Text>();
        string hintText = initialHint.text;
        Check(initial.nameInput != null && initial.nameInput.characterLimit == 12 && initial.nameInput.textComponent != null,
            "Initial name panel binds the authored legacy InputField and legacy Text");
        yield return Capture("initial-name");
        Check(initial.nameInput.text == "", "Initial name input starts empty rather than using the login username");
        initial.nameInput.text = "   "; initial.confirmButton.onClick.Invoke();
        initial.nameInput.text = "<b>bad</b>"; initial.confirmButton.onClick.Invoke();
        initial.nameInput.text = "名字测试一ABC"; initial.confirmButton.onClick.Invoke();
        Check(fake.PersonalCalls == 0 && flow.ViewState == LoginViewState.InitialName && flow.Message == DisplayNameRules.Error && initialHint.text == hintText,
            "Empty, rich-text and overweight mixed names are rejected while the authored Status stays unchanged");
        yield return Capture("initial-name-invalid");
        initial.nameInput.text = "名字测试一AB"; initial.nameInput.onEndEdit.Invoke(initial.nameInput.text);
        Check(fake.PersonalCalls == 0, "Legacy input losing focus does not submit the name");
        Canvas.ForceUpdateCanvases();
        var initialCanvas = initial.GetComponentInParent<Canvas>();
        var initialRect = (RectTransform)initial.confirmButton.transform;
        var pointer = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current)
        { position = RectTransformUtility.WorldToScreenPoint(initialCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : initialCanvas.worldCamera, initialRect.TransformPoint(initialRect.rect.center)) };
        var pointerHits = new List<UnityEngine.EventSystems.RaycastResult>(); UnityEngine.EventSystems.EventSystem.current.RaycastAll(pointer, pointerHits);
        Check(pointerHits.Count > 0 && UnityEngine.EventSystems.ExecuteEvents.GetEventHandler<UnityEngine.EventSystems.IPointerClickHandler>(pointerHits[0].gameObject) == initial.confirmButton.gameObject,
            "Initial name confirmation receives actual pointer raycasts without transparent-layer interception");
        initial.nameInput.text = "初始旅行者";
        var initialGate = fake.PersonalGate = new TaskCompletionSource<bool>(); fake.FailPersonal = true;
        initial.confirmButton.onClick.Invoke(); initial.confirmButton.onClick.Invoke(); flow.EnterGame();
        Check(flow.IsBusy && !initial.confirmButton.interactable && fake.PersonalCalls == 1 && SceneManager.GetActiveScene().name == "Login" && initialHint.text == hintText, "Pending initial-name save blocks duplicate confirmation and preserves the static rule hint");
        initialGate.SetResult(true); yield return Wait(() => !flow.IsBusy, "Initial name save failure");
        Check(flow.ViewState == LoginViewState.InitialName && initial.nameInput.text == "初始旅行者" && flow.Message.Contains("失败") && initialHint.text == hintText && account.Profile.personal == null,
            "Failed initial-name save stays on the panel and retains the input without applying a local-only name");
        yield return Capture("initial-name-retry");
        initial.confirmButton.onClick.Invoke();
        yield return Wait(() => SceneManager.GetActiveScene().name == "GameLobby", "Login enters GameLobby only after profile success");
        Check(account.Profile.DisplayName == "初始旅行者" && account.Username == "mvc_validation" && fake.PersonalCalls == 2 && fake.LoadCalls == 2, "Cloud-confirmed initial name enters the game once while preserving login identity and avoiding another load");
        fake.PersonalCalls = 0;
        var root = UnityEngine.Object.FindObjectOfType<LobbyUIRoot>(true);
        yield return Wait(() => root.Controller != null, "MVC composition after profile load");
        var controller = root.Controller;
        LobbyManager.Instance.dedicatedServerAddress = "127.0.0.1"; // Runtime clone only: custom discovery remains local.
        Check(fake.LastLoadScene == "Login" && fake.LoadCalls == 2 && controller.Model.Page == LobbyPage.MainMenu, "GameLobby reuses Login's profile and first displays MainMenu without a second read");
        Check(ActivePages(root) == 1, "Exactly one page visible after startup");
        Check(loadingStages.Contains("正在读取用户数据…") && loadingStages.Contains("用户数据读取完成，正在加载大厅…") && loadingStages.Contains("加载完成，正在进入大厅…"), "Authored Status reports all three loading stages");
        Check(progressCompletedInLogin, "Login renders real scene progress and TMP 100% before activating GameLobby");
        int instance = root.GetInstanceID();
        yield return Capture("main-menu");
        Click(root, LobbyPage.MainMenu, LobbyAction.Multiplayer);
        Check(controller.Model.Page == LobbyPage.Home && root.GetInstanceID() == instance, "Multiplayer opens HomePanel inside the same scene");
        Check(root.identity != null && (root.identity.uid != null ? root.identity.uid.text : root.identity.legacyUid.text) == "UID: " + AccountUid.Format(account.PublicUid), "Lobby displays the public numeric UID");
        yield return Capture("home");
        Click(root, LobbyPage.Home, LobbyAction.Back);
        Check(controller.Model.Page == LobbyPage.MainMenu && root.GetInstanceID() == instance, "HomePanel returns to MainMenu without scene reload");
        Click(root, LobbyPage.MainMenu, LobbyAction.Placeholder);
        Check(controller.IsModalOpen && !Page(root, LobbyPage.MainMenu).GetComponent<CanvasGroup>().interactable, "Modal blocks the underlying page");
        root.messageDialog.confirmButton.onClick.Invoke();
        Check(!controller.IsModalOpen && controller.Model.Page == LobbyPage.MainMenu, "Message closes without resetting navigation");
        Click(root, LobbyPage.MainMenu, LobbyAction.Exit); root.confirmDialog.cancelButton.onClick.Invoke();
        Check(!controller.IsModalOpen, "Exit confirmation can be cancelled");

        Click(root, LobbyPage.MainMenu, LobbyAction.Character);
        yield return Wait(() => root.customizationView.preview.image.texture != null, "3D customization preview");
        var saved = account.Profile.appearance;
        root.customizationView.next[0].onClick.Invoke();
        Check(controller.Draft.IsDirty && account.Profile.appearance.Equals(saved), "Customization modifies a draft without changing the account profile");
        yield return Capture("customization");
        controller.Back(); Check(controller.IsModalOpen, "Unsaved changes require a discard decision");
        root.confirmDialog.cancelButton.onClick.Invoke();
        Check(controller.Model.Page == LobbyPage.Customization && controller.Draft.IsDirty, "Cancelling discard preserves the draft");
        fake.SaveGate = new TaskCompletionSource<bool>();
        Click(root, LobbyPage.Customization, LobbyAction.SaveProfile);
        Check(account.IsBusy && !root.customizationView.saveButton.interactable, "Saving prevents duplicate submissions");
        fake.SaveGate.SetResult(true);
        yield return Wait(() => !account.IsBusy, "Save completion");
        Check(!controller.Draft.IsDirty && !account.Profile.appearance.Equals(saved) && fake.SaveCalls == 1, "Successful save applies the acknowledged cloud profile");
        root.customizationView.next[1].onClick.Invoke(); controller.Back(); root.confirmDialog.confirmButton.onClick.Invoke();
        Check(controller.Model.Page == LobbyPage.MainMenu && !controller.Draft.IsDirty, "Confirming discard returns to the prior page");
        for (int i = 0; i < 3; i++) { Click(root, LobbyPage.MainMenu, LobbyAction.Clothes); controller.Back(); }
        Check(controller.Model.Page == LobbyPage.MainMenu && fake.SaveCalls == 1, "Repeated open/close adds no save listeners or operations");

        Click(root, LobbyPage.MainMenu, LobbyAction.Multiplayer);
        fake.Profile.career = new PlayerCareerRecord { matches = 4, wins = 3, kills = 30, deaths = 10, headshotKills = 12 };
        Click(root, LobbyPage.Home, LobbyAction.Profile);
        var redesigned = (ProfilePanelView)Page(root, LobbyPage.Profile);
        Check(redesigned.overview.activeSelf && !redesigned.information.activeSelf && redesigned.firstPageButton.image.color == redesigned.selectedTabColor, "Avatar entry defaults to FirstPagePanel with yellow Home button");
        Check(fake.LoadCalls == 3 && redesigned.matchesText.text == "4" && redesigned.winsText.text == "3" && redesigned.killsText.text == "30" && redesigned.kdText.text == "3.00" && redesigned.deathsText.text == "10" && redesigned.headshotRateText.text == "40.0%", "Home renders freshly synchronized cloud career totals and ratios");
        Check(!redesigned.maleToggle.isOn && !redesigned.femaleToggle.isOn && !redesigned.genderIcon.enabled, "Legacy profile leaves gender unspecified without inventing saved data");
        redesigned.signatureInput.text = "今天也要赢！"; redesigned.signatureInput.onEndEdit.Invoke(redesigned.signatureInput.text);
        yield return Wait(() => !account.IsBusy, "Signature save");
        Check(account.Profile.personal.signature == "今天也要赢！" && fake.PersonalCalls == 1, "Signature commits through AccountClient and updates the acknowledged profile");
        yield return Capture("profile-first-page");
        Click(root, LobbyPage.Profile, LobbyAction.ProfileInformation);
        Check(!redesigned.overview.activeSelf && redesigned.information.activeSelf && redesigned.informationButton.image.color == redesigned.selectedTabColor && redesigned.firstPageButton.image.color != redesigned.selectedTabColor, "Information tab exclusively selects its page and yellow button");
        redesigned.editNameButton.onClick.Invoke();
        Check(redesigned.nameEditor.activeSelf && redesigned.nameInput.text == account.Profile.DisplayName, "Edit name opens the existing dialog with current display name");
        Check(redesigned.nameInput.characterLimit == 12, "Profile name editor has the same legacy input character ceiling");
        foreach (var invalidName in new[] { "", "名字测试一二三", "名字测试一ABC", "A_1" })
        {
            redesigned.nameInput.text = invalidName; redesigned.confirmNameButton.onClick.Invoke();
            Check(fake.PersonalCalls == 1 && controller.IsModalOpen && redesigned.nameEditor.activeSelf, "Profile rejects invalid rename without a cloud write: " + invalidName);
            root.messageDialog.confirmButton.onClick.Invoke();
        }
        int characterRevision = account.Profile.revision; string loginName = account.Username;
        redesigned.nameInput.text = "测试旅行者";
        var personalGate = fake.PersonalGate = new TaskCompletionSource<bool>();
        redesigned.confirmNameButton.onClick.Invoke(); redesigned.confirmNameButton.onClick.Invoke();
        Check(account.IsBusy && fake.PersonalCalls == 2 && account.Profile.DisplayName != "测试旅行者", "Pending name save blocks duplicate requests and waits for cloud confirmation");
        personalGate.SetResult(true); yield return Wait(() => !account.IsBusy, "Name save");
        Check(!redesigned.nameEditor.activeSelf && redesigned.playerName.text == "测试旅行者" && redesigned.overviewName.text == "测试旅行者" && account.Username == loginName && account.Profile.revision == characterRevision, "Nickname updates both pages while preserving login identity and character revision");
        redesigned.femaleToggle.isOn = true; yield return Wait(() => !account.IsBusy, "Female gender save");
        Check(account.Profile.personal.gender == 2 && redesigned.femaleToggle.isOn && !redesigned.maleToggle.isOn && redesigned.genderIcon.sprite == redesigned.femaleIcon, "Female gender persists and updates the home icon");
        redesigned.maleToggle.isOn = true; yield return Wait(() => !account.IsBusy, "Male gender save");
        Check(account.Profile.personal.gender == 1 && redesigned.maleToggle.isOn && !redesigned.femaleToggle.isOn && redesigned.genderIcon.sprite == redesigned.maleIcon, "Gender selection stays mutually exclusive");
        redesigned.editNameButton.onClick.Invoke(); controller.Back();
        Check(!redesigned.nameEditor.activeSelf && controller.Model.Page == LobbyPage.Profile, "Escape closes name editor before navigating away");
        Click(root, LobbyPage.Profile, LobbyAction.ProfileOverview);
        fake.FailPersonal = true;
        redesigned.signatureInput.text = "失败后保留输入"; redesigned.signatureInput.onEndEdit.Invoke(redesigned.signatureInput.text);
        yield return Wait(() => !account.IsBusy, "Rejected signature save");
        Check(controller.IsModalOpen && account.Profile.personal.signature == "今天也要赢！" && redesigned.signatureInput.text == "失败后保留输入", "Failed save preserves input and authoritative cloud signature with visible feedback");
        root.messageDialog.confirmButton.onClick.Invoke();
        redesigned.signatureInput.onEndEdit.Invoke(redesigned.signatureInput.text); yield return Wait(() => !account.IsBusy, "Retry signature save");
        Check(account.Profile.personal.signature == "失败后保留输入", "Failed signature can be retried successfully");
        Click(root, LobbyPage.Profile, LobbyAction.ProfileInformation); Click(root, LobbyPage.Profile, LobbyAction.Avatar);
        var profile = (ProfilePanelView)Page(root, LobbyPage.Profile);
        Check(profile.information.activeSelf && profile.avatarPicker.activeSelf && profile.choiceButtons.Length == Mathf.Min(32, root.avatars.avatars.Length), "Personal information and configured avatar picker work");
        var scroll = profile.avatarScroll;
        Check(scroll != null && scroll.viewport != scroll.content && scroll.content.parent == scroll.viewport && scroll.viewport.GetComponent<UnityEngine.UI.RectMask2D>() != null && !scroll.horizontal && scroll.vertical && scroll.verticalScrollbar != null && scroll.verticalScrollbar.direction == UnityEngine.UI.Scrollbar.Direction.BottomToTop && scroll.verticalScrollbar.gameObject.activeInHierarchy && scroll.verticalScrollbar.targetGraphic.color.a > 0, "Avatar picker has a separate masked viewport and vertical scrollbar");
        Canvas.ForceUpdateCanvases(); yield return null;
        Check(scroll.content.rect.height > scroll.viewport.rect.height && scroll.verticalScrollbar.size < 1f, "Avatar grid grows past the viewport with a scrollable scrollbar handle");
        scroll.verticalScrollbar.value = 0f; yield return null; yield return null;
        Check(scroll.content.anchoredPosition.y > 0 && profile.avatarChoices[0].image.canvasRenderer.cull && !profile.avatarChoices[profile.avatarChoices.Length - 1].image.canvasRenderer.cull, "Bottom scroll reveals final avatars and clips earlier rows");
        yield return Capture("avatar-picker-bottom");
        scroll.verticalNormalizedPosition = 1f; yield return null;
        scroll.OnScroll(new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current) { scrollDelta = new Vector2(0, -1) });
        Check(scroll.content.anchoredPosition.y > 0, "Mouse wheel scrolls the clipped avatar grid");
        profile.choiceButtons[1].onClick.Invoke();
        yield return Wait(() => !account.IsBusy, "Avatar save");
        Check(account.Profile.avatarId == 1 && !profile.avatarPicker.activeSelf && fake.AvatarCalls == 1, "Preset avatar is saved and picker closes");
        yield return Capture("profile");
        Click(root, LobbyPage.Profile, LobbyAction.Avatar); yield return Capture("avatar-picker");
        Click(root, LobbyPage.Profile, LobbyAction.CloseAvatarPicker);
        controller.Back();
        Check(controller.Model.Page == LobbyPage.Home && Page(root, LobbyPage.Home).playerName.text == "测试旅行者", "Profile returns to Home with the saved nickname");
        fake.Profile.personal.displayName = "跨设备新昵称";
        fake.Profile.personal.signature = "另一台设备已更新";
        Click(root, LobbyPage.Home, LobbyAction.Profile);
        Check(profile.overviewName.text == "跨设备新昵称" && profile.signatureInput.text == "另一台设备已更新", "Reopening profile reads personal edits made on another device as well as career records");
        Check(profile.overview.activeSelf && !profile.information.activeSelf && !profile.avatarPicker.activeSelf && profile.firstPageButton.image.color == profile.selectedTabColor && fake.LoadCalls == 4, "Reopening via avatar resets Home selection and refreshes cloud career without duplicate listeners");
        controller.Back();

        var delayedCreate = fake.CreateGate = new TaskCompletionSource<UgsPvpReply>();
        Click(root, LobbyPage.Home, LobbyAction.CreateParty);
        Check(PartyLobbyClient.Instance.IsBusy && controller.Model.Page == LobbyPage.Home && !root.partyView.gameObject.activeSelf, "Pending party creation keeps HomePanel visible");
        Click(root, LobbyPage.Home, LobbyAction.CreateParty);
        Check(fake.CreateCalls == 1, "Repeated start clicks submit one party request");
        delayedCreate.SetResult(Reply("{\"code\":\"test_failure\",\"message\":\"离线验证建队失败\"}", 503));
        yield return Wait(() => !PartyLobbyClient.Instance.IsBusy, "Failed party creation");
        Check(controller.Model.Page == LobbyPage.Home && !string.IsNullOrEmpty(Page(root, LobbyPage.Home).status.text), "Failed creation remains on HomePanel with an error");
        Click(root, LobbyPage.Home, LobbyAction.CreateParty);
        yield return Wait(() => !PartyLobbyClient.Instance.IsBusy, "Successful party creation");
        Check(controller.Model.Page == LobbyPage.Party && ActivePages(root) == 1, "Confirmed party opens the five-player panel");
        Check(root.partyView.members[0].playerName.text == account.Profile.DisplayName && root.partyView.members[0].playerName.text != account.Username,
            "Party slot displays the saved game name instead of the login username");
        Check(root.partyCharacters[0].gameObject.activeSelf && root.partyCharacters.Skip(1).All(character => !character.gameObject.activeSelf), "Only occupied member previews are visible");
        yield return Capture("party");
        Click(root, LobbyPage.Party, LobbyAction.Primary);
        yield return Wait(() => !PartyLobbyClient.Instance.IsBusy, "Start matching");
        Check(PartyLobbyClient.Instance.Party.state == "Matching", "Owner primary button starts matching");
        Click(root, LobbyPage.Party, LobbyAction.Primary);
        yield return Wait(() => !PartyLobbyClient.Instance.IsBusy, "Cancel matching");
        Check(PartyLobbyClient.Instance.Party.state == "Waiting", "Matching can be cancelled");
        controller.Back(); yield return Wait(() => !PartyLobbyClient.Instance.IsBusy, "Leave party");
        Check(controller.Model.Page == LobbyPage.Home && PartyLobbyClient.Instance.Party == null, "Party return leaves the party and restores HomePanel");
        Click(root, LobbyPage.Home, LobbyAction.Rooms);
        yield return Wait(() => root.roomsView.content.childCount > 0, "Party room list");
        Check(PartyLobbyClient.Instance.BrowseRooms, "Room queries enabled only on room-list page");
        yield return Capture("rooms");
        var cardRect = root.roomsView.content.GetChild(0).GetComponent<RectTransform>();
        Check(cardRect.rect.width > 0 && cardRect.rect.width <= ((RectTransform)root.roomsView.content).rect.width + 0.1f, "Room cards fit the available list width");
        var joinCorners = new Vector3[4]; root.roomsView.content.GetChild(0).GetComponent<RoomEntryUI>().joinButton.GetComponent<RectTransform>().GetWorldCorners(joinCorners);
        var canvasCamera = root.roomsView.GetComponentInParent<Canvas>().worldCamera;
        Check(RectTransformUtility.WorldToScreenPoint(canvasCamera, joinCorners[2]).x <= Screen.width + 1f, "Room-card join button remains fully on screen");
        root.roomsView.content.GetChild(0).GetComponent<RoomEntryUI>().joinButton.onClick.Invoke();
        yield return Wait(() => !PartyLobbyClient.Instance.IsBusy && controller.Model.Page == LobbyPage.Party, "Join existing party");
        Check(!PartyLobbyClient.Instance.IsOwner && !PartyLobbyClient.Instance.BrowseRooms, "Join opens member UI and stops room browsing");
        Click(root, LobbyPage.Party, LobbyAction.Primary); yield return Wait(() => !PartyLobbyClient.Instance.IsBusy, "Member ready");
        Check(PartyLobbyClient.Instance.LocalReady, "Member primary button toggles readiness");
        controller.Back(); yield return Wait(() => !PartyLobbyClient.Instance.IsBusy, "Leave joined party");
        controller.Back();
        Click(root, LobbyPage.MainMenu, LobbyAction.CustomPvp);
        Check(controller.Model.Page == LobbyPage.CustomRoom && root.customRoomView.teamSlots.Length == 10, "Custom room uses a prefab with ten baked team slots");
        yield return Capture("custom-room");
        Click(root, LobbyPage.CustomRoom, LobbyAction.CreateRoom);
        yield return Wait(() => !controller.Model.IsBusy, "Local custom-room discovery timeout");
        Check(!string.IsNullOrEmpty(controller.Model.Status), "Unavailable custom server produces a recoverable status");
        controller.Back();
        Check(controller.Model.Page == LobbyPage.MainMenu && ActivePages(root) == 1, "Custom room returns to MainMenu");
        int loads = fake.LoadCalls;
        var reload = SceneManager.LoadSceneAsync("GameLobby");
        yield return Wait(() => reload.isDone, "Scene activation");
        yield return Wait(() => UnityEngine.Object.FindObjectOfType<LobbyUIRoot>()?.Controller != null, "Lobby reload");
        root = UnityEngine.Object.FindObjectOfType<LobbyUIRoot>();
        Check(fake.LoadCalls == loads && root.Controller.Model.Page == LobbyPage.MainMenu && root.GetInstanceID() != instance, "Reentering lobby retains authenticated profile and recreates local navigation");
        Check(UnityEngine.Object.FindObjectsOfType<Unity.Netcode.NetworkManager>().Length == 1, "Scene reload retains exactly one network manager");
        Check(UnityEngine.Object.FindObjectsOfType<UnityEngine.EventSystems.EventSystem>().Length == 1, "Exactly one active EventSystem");
        Check(UnityEngine.Object.FindObjectsOfType<AudioListener>().Count(listener => listener.isActiveAndEnabled) == 1, "Exactly one active audio listener");
        LoginSceneBootstrap.ReturnToLogin();
        yield return Wait(() => SceneManager.GetActiveScene().name == "Login" && UnityEngine.Object.FindObjectOfType<LoginSceneController>() != null, "Return to Login for session failure");
        yield return null;
        flow = UnityEngine.Object.FindObjectOfType<LoginSceneController>();
        flow.enterGameAfterLogin = false;
        bool promptedAgain = false;
        flow.Changed += () => promptedAgain |= flow.ViewState == LoginViewState.InitialName;
        flow.EnterGame();
        yield return Wait(() => SceneManager.GetActiveScene().name == "GameLobby", "Existing named account reenters the game");
        Check(!promptedAgain && account.Profile.DisplayName == "跨设备新昵称", "An account with a cloud name skips InitialNamePanel on subsequent entry");
        LoginSceneBootstrap.ReturnToLogin();
        yield return Wait(() => SceneManager.GetActiveScene().name == "Login" && UnityEngine.Object.FindObjectOfType<LoginSceneController>() != null, "Login for expired session verification");
        yield return null;
        flow = UnityEngine.Object.FindObjectOfType<LoginSceneController>(); flow.enterGameAfterLogin = false;
        var deniedGate = new TaskCompletionSource<PlayerProfileData>(); fake.LoadGate = deniedGate;
        loads = fake.LoadCalls;
        flow.EnterGame();
        yield return Wait(() => fake.LoadCalls == loads + 1, "Login refreshes its profile on reentry");
        deniedGate.SetException(new UgsAccountException("登录已失效，请重新登录。", true));
        yield return Wait(() => !flow.IsBusy, "Expired session response");
        Check(SceneManager.GetActiveScene().name == "Login" && flow.ViewState == LoginViewState.Login && !account.HasSession && account.Profile == null && flow.Message.Contains("失效"), "Expired profile session stays in Login and clears old account data");
        flow.Login("mvc_validation", "Validation9!Pass");
        yield return Wait(() => !flow.IsBusy, "Session-only login for direct-entry guard");
        var directEntry = SceneManager.LoadSceneAsync("GameLobby");
        yield return Wait(() => directEntry.isDone, "Direct lobby activation");
        yield return Wait(() => SceneManager.GetActiveScene().name == "Login", "Missing profile returns to Login");
        yield return null;
        flow = UnityEngine.Object.FindObjectOfType<LoginSceneController>();
        Check(fake.LoadCalls == loads + 1 && flow.ViewState == LoginViewState.Ready && account.HasSession && !account.IsAuthenticated, "Direct lobby entry without a profile returns to Login instead of downloading in GameLobby");
    }
    private static int ActivePages(LobbyUIRoot root) => root.pages.Count(page => page.gameObject.activeInHierarchy);
    private static LobbyPageView Page(LobbyUIRoot root, LobbyPage page) => root.pages.First(view => view.page == page);
    private static void Click(LobbyUIRoot root, LobbyPage page, LobbyAction action) => Page(root, page).buttons.First(binding => binding.action == action).button.onClick.Invoke();
    private static IEnumerator Wait(Func<bool> predicate, string description)
    {
        double deadline = EditorApplication.timeSinceStartup + 15;
        while (!predicate()) { if (EditorApplication.timeSinceStartup > deadline) throw new InvalidOperationException("Timed out: " + description); yield return null; }
    }
    private static IEnumerator Capture(string name)
    {
        double until = EditorApplication.timeSinceStartup + 0.9;
        int frame = Time.frameCount;
        while (EditorApplication.timeSinceStartup < until || Time.frameCount < frame + 3) yield return null;
        ScreenCapture.CaptureScreenshot(Path.GetFullPath("tmp/LobbyMvc/" + name + ".png"));
        yield return null; yield return null;
    }
    private static void Check(bool value, string description)
    { if (!value) throw new InvalidOperationException(description); Read().checks.Add(description); Store(); }
    private static void Finish(string error)
    {
        if (!Running || Stage == "stopping") return;
        if (error != null) Read().errors.Add(error);
        Factory.SetValue(null, originalFactory); steps.Clear(); Stage = "stopping"; Store();
        EditorApplication.isPlaying = false;
    }
    private static void Restore()
    {
        try
        {
            Environment.SetEnvironmentVariable("FPS_ACCOUNT_BACKEND", SessionState.GetBool(Prefix + "hadEnvironment", false) ? SessionState.GetString(Prefix + "environment", "") : null);
            Application.runInBackground = SessionState.GetBool(Prefix + "background", false);
            string playStart = SessionState.GetString(Prefix + "previousPlayStart", "");
            EditorSceneManager.playModeStartScene = string.IsNullOrEmpty(playStart) ? null : AssetDatabase.LoadAssetAtPath<SceneAsset>(playStart);
            var original = SceneManager.GetActiveScene();
            if (original.path != SessionState.GetString(Prefix + "originalScene", "") || original.isDirty != SessionState.GetBool(Prefix + "originalDirty", false))
                throw new InvalidOperationException("The Editor did not restore its original scene and unsaved state.");
            Read().restored = true;
        }
        catch (Exception exception) { Read().errors.Add("Restore: " + exception.Message); }
        Read().success = Read().restored && Read().errors.Count == 0; Store();
        Directory.CreateDirectory("tmp/LobbyMvc"); File.WriteAllText(Output, JsonUtility.ToJson(Read(), true));
        SessionState.SetBool(Prefix + "running", false); Stage = "done";
        Debug.Log("[Lobby MVC Validation] " + (Read().success ? "PASS" : "FAILED") + ": " + Read().checks.Count + " checks; " + Output);
    }
    private static void Log(string message, string trace, LogType type)
    {
        if (!Running || Stage == "stopping" || type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
        bool baseline = message.Contains("Failed to late initialize plugin module 'VisualScripting.Core.BoltCoreConfiguration'")
            || trace.Contains("Unity.VisualScripting.Codebase.IncludeInSettings") || trace.Contains("Unity.VisualScripting.XmlDocumentation.BackgroundWork");
        (baseline ? Read().baselineErrors : Read().errors).Add(baseline ? "Known Visual Scripting initialization failure" : message.Split('\n')[0]); Store();
    }
    private static Report Read() => report ?? (report = JsonUtility.FromJson<Report>(SessionState.GetString(Prefix + "report", "{}")));
    private static void Store() => SessionState.SetString(Prefix + "report", JsonUtility.ToJson(Read()));
    private static PlayerProfileData Clone(PlayerProfileData data) => data == null ? null : new PlayerProfileData
    {
        accountId = data.accountId, uid = data.uid, username = data.username, avatarId = data.avatarId,
        revision = data.revision, appearance = data.appearance, loadout = data.loadout, personalRevision = data.personalRevision,
        personal = data.personal == null ? null : new PersonalProfileData { displayName = data.personal.displayName, gender = data.personal.gender, signature = data.personal.signature },
        career = data.career == null ? null : new PlayerCareerRecord { matches = data.career.matches, wins = data.career.wins, kills = data.career.kills, deaths = data.career.deaths, headshotKills = data.career.headshotKills }
    };
    private static UgsPvpReply Reply(string body, int status = 200) => new UgsPvpReply { ok = true, state = "done", status = status, body = body };

    private sealed class FakeGateway : IUgsAccountGateway, IUgsPvpGateway, IUgsPersonalProfileGateway
    {
        public bool IsSignedIn { get; private set; }
        public string PlayerId => IsSignedIn ? "mvc-offline-player" : "";
        public int PublicUid => IsSignedIn ? Profile.uid : 0;
        public bool HasLegacyUid => false;
        public string AccessToken => IsSignedIn ? "offline-validation" : "";
        public event Action SessionExpired;
        public PlayerProfileData Profile = new PlayerProfileData { accountId = "mvc-offline-player", uid = AccountUid.First, username = "mvc_validation", avatarId = 0 };
        public TaskCompletionSource<PlayerProfileData> LoadGate = new TaskCompletionSource<PlayerProfileData>();
        public TaskCompletionSource<bool> SaveGate, PersonalGate;
        public bool FailPersonal;
        public int PersonalCalls;
        public TaskCompletionSource<UgsPvpReply> CreateGate;
        public int LoadCalls, SaveCalls, AvatarCalls, CreateCalls;
        public string LastLoadScene;
        private PartyData party;
        public Task InitializeAsync() => Task.CompletedTask;
        public Task SignUpAsync(string username, string password) { IsSignedIn = true; return Task.CompletedTask; }
        public Task SignInAsync(string username, string password) { IsSignedIn = true; return Task.CompletedTask; }
        public void SignOut() { IsSignedIn = false; }
        public Task<PlayerProfileData> LoadProfileAsync(string username)
        {
            LoadCalls++; LastLoadScene = SceneManager.GetActiveScene().name;
            if (LoadGate == null) return Task.FromResult(Clone(Profile));
            var gate = LoadGate; LoadGate = null; return gate.Task;
        }
        public async Task<PlayerProfileData> SaveProfileAsync(string username, int revision, CharacterAppearanceData appearance, PvpLoadoutData loadout)
        { SaveCalls++; if (SaveGate != null) await SaveGate.Task; Profile.appearance = appearance; Profile.loadout = loadout; Profile.revision++; return Clone(Profile); }
        public Task<PlayerProfileData> SaveAvatarAsync(string username, int revision, byte avatarId)
        { AvatarCalls++; Profile.avatarId = avatarId; Profile.revision++; return Task.FromResult(Clone(Profile)); }
        public async Task<PlayerProfileData> SavePersonalProfileAsync(string username, int revision, PersonalProfileData personal)
        {
            PersonalCalls++;
            if (PersonalGate != null) { var gate = PersonalGate; PersonalGate = null; await gate.Task; }
            if (FailPersonal) { FailPersonal = false; throw new UgsAccountException("测试资料保存失败"); }
            Profile.personal = JsonUtility.FromJson<PersonalProfileData>(JsonUtility.ToJson(personal)); Profile.personalRevision++;
            return Clone(Profile);
        }
        public Task<UgsPvpReply> ExecutePvpAsync(string operation, string username, Dictionary<string, object> args)
        {
            if (operation == "create")
            {
                CreateCalls++;
                if (CreateGate != null) { var gate = CreateGate; CreateGate = null; return gate.Task; }
                party = Party(true);
            }
            if (operation == "rooms") return Task.FromResult(Reply(JsonUtility.ToJson(new PartyRoomListData { rooms = new[] { Party(false) } })));
            if (operation == "join") party = Party(false);
            if (operation == "ready" && party != null) foreach (var member in party.members) if (member.profile.accountId == PlayerId) member.ready = !member.ready;
            if (operation == "queue" && party != null) party.state = "Matching";
            if (operation == "cancel" && party != null) party.state = "Waiting";
            if (operation == "leave") party = null;
            return Task.FromResult(Reply(party != null ? JsonUtility.ToJson(new PartyStateData { party = party }) : "{\"party\":null,\"message\":\"\"}"));
        }
        private PartyData Party(bool owner) => new PartyData { id = "mvc-party", name = "团队对抗", mapName = "测试地图1", state = "Waiting", ownerAccountId = owner ? PlayerId : "offline-owner",
            members = new[] { new PartyMemberData { profile = Clone(Profile), ready = owner } } };
        public void Dispose() { IsSignedIn = false; SessionExpired = null; }
    }
}
