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
        if (scene.path != "Assets/Scenes/GameLobby.unity" || scene.isDirty || SceneManager.sceneCount != 1)
            throw new InvalidOperationException("Open the saved GameLobby scene before validation.");
        report = new Report(); ValidateAssets(); Store();
        SessionState.SetString(Prefix + "environment", Environment.GetEnvironmentVariable("FPS_ACCOUNT_BACKEND") ?? "");
        SessionState.SetBool(Prefix + "hadEnvironment", Environment.GetEnvironmentVariable("FPS_ACCOUNT_BACKEND") != null);
        SessionState.SetBool(Prefix + "background", Application.runInBackground);
        SessionState.SetString(Prefix + "started", DateTime.UtcNow.Ticks.ToString());
        SessionState.SetBool(Prefix + "running", true); Stage = "starting";
        Environment.SetEnvironmentVariable("FPS_ACCOUNT_BACKEND", "UnityGamingServices");
        EditorSceneManager.OpenScene("Assets/Scenes/Login.unity", OpenSceneMode.Single);
        EditorApplication.isPaused = false; EditorApplication.isPlaying = true;
    }
    public static void Audit()
    {
        report = new Report(); ValidateAssets(); report.success = report.errors.Count == 0;
        File.WriteAllText("tmp/LobbyMvc/asset-validation.json", JsonUtility.ToJson(report, true));
    }
    private static void ValidateAssets()
    {
        var root = UnityEngine.Object.FindObjectOfType<LobbyUIRoot>(true);
        Check(root != null && root.pages.Length == 7 && root.pages.Select(page => page.page).Distinct().Count() == 7, "Seven unique scene panel bindings");
        Check(root.pages.All(page => PrefabUtility.GetCorrespondingObjectFromSource(page) != null), "Every page is a prefab instance");
        Check(root.messageDialog != null && root.confirmDialog != null && root.loading != null, "Modal and loading layers are bound");
        Check(root.partyCharacters.Length == 5 && root.partyCharacters.All(item => item != null), "Five scene character previews remain bound");
        Check(root.roomsView.entryPrefab.mapName != null && root.roomsView.entryPrefab.roomState != null && root.roomsView.entryPrefab.avatars.All(avatar => avatar != null), "Room cards bind map, state, join and five avatar fields");
        uint[] missingCharacters;
        Check(root.partyView.status.font.HasCharacters("正在创建队伍等待成员准备团队对抗", out missingCharacters, true, true), "Lobby status fonts cover required Chinese characters");
        var clients = GameFlowBuildScenes.Get(false); var servers = GameFlowBuildScenes.Get(true);
        Check(clients[0].EndsWith("Login.unity") && clients.Count(path => path.EndsWith("GameLobby.unity")) == 1 && !clients.Any(path => path.EndsWith("GameStart.unity")), "Client build uses Login, one GameLobby and no GameStart");
        Check(servers[0].EndsWith("GameLobby.unity") && !servers.Any(path => path.EndsWith("Login.unity")), "Dedicated-server build starts in GameLobby");
        foreach (string path in Directory.GetFiles("Assets/UI/Prefab/Mvc", "*.prefab"))
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
        yield return Wait(() => UnityEngine.Object.FindObjectOfType<LoginSceneController>() != null, "Login scene initialization");
        var flow = UnityEngine.Object.FindObjectOfType<LoginSceneController>();
        flow.enterGameAfterLogin = false;
        flow.Login("mvc_validation", "Validation9!Pass");
        yield return Wait(() => !flow.IsBusy, "Login completion");
        var account = AccountClient.Instance;
        Check(account.HasSession && account.Profile == null, "Login establishes a session without loading a profile");
        flow.EnterGame();
        Check(flow.ViewState == LoginViewState.Loading, "Login retains its authored loading panel");
        yield return Wait(() => SceneManager.GetActiveScene().name == "GameLobby" && fake.LoadCalls > 0, "Direct Login to GameLobby");
        var root = UnityEngine.Object.FindObjectOfType<LobbyUIRoot>(true);
        Check(root != null && root.Controller == null && root.loading.activeInHierarchy && root.pages.All(page => !page.gameObject.activeInHierarchy), "Cloud profile gate blocks all pages before data arrives");
        fake.LoadGate.SetResult(Clone(fake.Profile));
        yield return Wait(() => root.Controller != null, "MVC composition after profile load");
        var controller = root.Controller;
        LobbyManager.Instance.dedicatedServerAddress = "127.0.0.1"; // Runtime clone only: custom discovery remains local.
        Check(fake.LastLoadScene == "GameLobby" && controller.Model.Page == LobbyPage.MainMenu, "GameLobby loads profile and first displays MainMenu");
        Check(!root.loading.activeSelf && ActivePages(root) == 1, "Exactly one page visible after startup");
        int instance = root.GetInstanceID();
        yield return Capture("main-menu");
        Click(root, LobbyPage.MainMenu, LobbyAction.Multiplayer);
        Check(controller.Model.Page == LobbyPage.Home && root.GetInstanceID() == instance, "Multiplayer opens HomePanel inside the same scene");
        Check(root.identity != null && (root.identity.uid != null ? root.identity.uid.text : root.identity.legacyUid.text).Contains(account.AccountId), "Lobby UID displays the real account identity");
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
        Click(root, LobbyPage.Home, LobbyAction.Profile);
        Click(root, LobbyPage.Profile, LobbyAction.ProfileInformation); Click(root, LobbyPage.Profile, LobbyAction.Avatar);
        var profile = (ProfilePanelView)Page(root, LobbyPage.Profile);
        Check(profile.information.activeSelf && profile.avatarPicker.activeSelf && profile.choiceButtons.Length == Mathf.Min(32, root.avatars.avatars.Length), "Personal information and configured avatar picker work");
        profile.choiceButtons[1].onClick.Invoke();
        yield return Wait(() => !account.IsBusy, "Avatar save");
        Check(account.Profile.avatarId == 1 && !profile.avatarPicker.activeSelf && fake.AvatarCalls == 1, "Preset avatar is saved and picker closes");
        yield return Capture("profile");
        Click(root, LobbyPage.Profile, LobbyAction.Avatar); yield return Capture("avatar-picker");
        Click(root, LobbyPage.Profile, LobbyAction.CloseAvatarPicker);
        controller.Back();
        Check(controller.Model.Page == LobbyPage.Home, "Profile returns to its calling page");

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
            EditorSceneManager.OpenScene("Assets/Scenes/GameLobby.unity", OpenSceneMode.Single);
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
        accountId = data.accountId, username = data.username, avatarId = data.avatarId,
        revision = data.revision, appearance = data.appearance, loadout = data.loadout
    };
    private static UgsPvpReply Reply(string body, int status = 200) => new UgsPvpReply { ok = true, state = "done", status = status, body = body };

    private sealed class FakeGateway : IUgsAccountGateway, IUgsPvpGateway
    {
        public bool IsSignedIn { get; private set; }
        public string PlayerId => IsSignedIn ? "mvc-offline-player" : "";
        public string AccessToken => IsSignedIn ? "offline-validation" : "";
        public event Action SessionExpired;
        public PlayerProfileData Profile = new PlayerProfileData { accountId = "mvc-offline-player", username = "mvc_validation", avatarId = 0 };
        public TaskCompletionSource<PlayerProfileData> LoadGate = new TaskCompletionSource<PlayerProfileData>();
        public TaskCompletionSource<bool> SaveGate;
        public TaskCompletionSource<UgsPvpReply> CreateGate;
        public int LoadCalls, SaveCalls, AvatarCalls, CreateCalls;
        public string LastLoadScene;
        private PartyData party;
        public Task InitializeAsync() => Task.CompletedTask;
        public Task SignUpAsync(string username, string password) { IsSignedIn = true; return Task.CompletedTask; }
        public Task SignInAsync(string username, string password) { IsSignedIn = true; return Task.CompletedTask; }
        public void SignOut() { IsSignedIn = false; }
        public Task<PlayerProfileData> LoadProfileAsync(string username)
        { LoadCalls++; LastLoadScene = SceneManager.GetActiveScene().name; return LoadCalls == 1 ? LoadGate.Task : Task.FromResult(Clone(Profile)); }
        public async Task<PlayerProfileData> SaveProfileAsync(string username, int revision, CharacterAppearanceData appearance, PvpLoadoutData loadout)
        { SaveCalls++; if (SaveGate != null) await SaveGate.Task; Profile.appearance = appearance; Profile.loadout = loadout; Profile.revision++; return Clone(Profile); }
        public Task<PlayerProfileData> SaveAvatarAsync(string username, int revision, byte avatarId)
        { AvatarCalls++; Profile.avatarId = avatarId; Profile.revision++; return Task.FromResult(Clone(Profile)); }
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
