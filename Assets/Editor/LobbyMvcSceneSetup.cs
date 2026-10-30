using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FPS.UI;
using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

/// <summary>One-time migration of the open authored lobby. Geometry, artwork, fonts and anchored layouts are retained.</summary>
public static class LobbyMvcSceneSetup
{
    private const string Prefabs = "Assets/UI/Prefab/Mvc";
    [MenuItem("Tools/FPS/Lobby/Migrate Open Scene to MVC Prefabs")]
    public static void Apply()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (EditorApplication.isPlayingOrWillChangePlaymode || scene.path != "Assets/Scenes/GameLobby.unity")
            throw new InvalidOperationException("Only the open, stopped GameLobby may be migrated.");
        if (Find<LobbyUIRoot>(scene) != null) throw new InvalidOperationException("This scene is already migrated. Use the MVC validation command.");
        var main = Required(Find<MainMenuUI>(scene), "MainMenuUI");
        var home = Required(Find<MultiplayerLobbyUI>(scene), "MultiplayerLobbyUI");
        var party = Required(Find<PartyLobbyUI>(scene), "PartyLobbyUI");
        var custom = Required(Find<LobbyMenuUI>(scene), "LobbyMenuUI");
        var tip = Required(Find<TipMenuUI>(scene), "TipMenuUI");
        var exit = Required(Find<ExitMenuUI>(scene), "ExitMenuUI");
        var mainCanvas = Required(main.GetComponentInParent<Canvas>(), "Main menu Canvas");
        var homeCanvas = Required(home.homePanel.GetComponentInParent<Canvas>(), "Lobby Canvas");
        var profileObject = Required(homeCanvas.transform.Find("PersonalHomepage"), "PersonalHomepage").gameObject;
        Directory.CreateDirectory("tmp/LobbyMvc/before");
        string backup = Path.GetFullPath("tmp/LobbyMvc/before/GameLobby-before-migration-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".unity");
        if (!EditorSceneManager.SaveScene(scene, backup, true)) throw new InvalidOperationException("Could not back up the open scene.");
        Directory.CreateDirectory(Prefabs); AssetDatabase.Refresh();

        var legacy = new UnityEngine.Object[] { main, home, party, custom, tip, exit };
        foreach (var item in legacy) Unpack(((Component)item).gameObject);
        Unpack(profileObject); Unpack(home.homePanel); Unpack(party.partyPanel); Unpack(party.roomPanel);
        foreach (var root in scene.GetRootGameObjects())
        foreach (var button in root.GetComponentsInChildren<UnityEngine.UI.Button>(true))
            for (int i = button.onClick.GetPersistentEventCount() - 1; i >= 0; i--)
                if (legacy.Contains(button.onClick.GetPersistentTarget(i))) UnityEventTools.RemovePersistentListener(button.onClick, i);

        var mainView = Page<LobbyPageView>(main.gameObject, LobbyPage.MainMenu);
        mainView.buttons = Bindings(
            Bind(main.btnEnterLobby, LobbyAction.Multiplayer), Bind(main.btnSinglePlayer, LobbyAction.Pve),
            Bind(main.btnTrainingRange, LobbyAction.Placeholder), Bind(main.btnCustomRoom, LobbyAction.CustomPvp),
            Bind(main.btnCharacter, LobbyAction.Character), Bind(main.btnClothes, LobbyAction.Clothes),
            Bind(main.btnSettings, LobbyAction.Placeholder), Bind(main.btnAchievements, LobbyAction.Placeholder),
            Bind(main.btnAuthor, LobbyAction.Placeholder), Bind(main.btnLanguage, LobbyAction.Placeholder),
            Bind(main.btnVoice, LobbyAction.Placeholder), Bind(main.btnExit, LobbyAction.Exit));

        var homeView = Page<LobbyPageView>(home.homePanel, LobbyPage.Home);
        var homeBindings = new List<LobbyButtonBinding> {
            Bind(home.btnStartGame, LobbyAction.CreateParty), Bind(home.btnRoomList, LobbyAction.Rooms),
            Bind(home.btnReturnMainMenu, LobbyAction.Back), Bind(home.btnCharacter, LobbyAction.Character),
            Bind(home.btnClothes, LobbyAction.Clothes), Bind(home.btnLoadout, LobbyAction.Loadout), Bind(home.btnAccount, LobbyAction.Profile) };
        AddUnboundButtons(homeView.transform, homeBindings, LobbyAction.Placeholder);
        homeView.playerName = home.tmpPlayerName; homeView.status = home.tmpStatus; homeView.avatar = party.homeAvatar;
        FitName(homeView.playerName);
        var portrait = homeView.transform.Find("TopMenu/Self/ProfilePicture");
        if (portrait != null)
        {
            var button = portrait.GetComponent<UnityEngine.UI.Button>();
            if (button == null) button = portrait.gameObject.AddComponent<UnityEngine.UI.Button>();
            button.targetGraphic = portrait.GetComponent<UnityEngine.UI.Graphic>();
            homeBindings.RemoveAll(binding => binding != null && binding.button == button);
            homeBindings.Add(Bind(button, LobbyAction.Profile));
        }
        homeView.buttons = Bindings(homeBindings.ToArray());

        var partyView = Page<PartyPanelView>(party.partyPanel, LobbyPage.Party);
        partyView.primaryButton = party.primaryButton; partyView.primaryLabel = party.primaryLabel; partyView.leaveLabel = party.leaveLabel;
        partyView.primaryTmpLabel = party.primaryTmpLabel; partyView.leaveTmpLabel = party.leaveTmpLabel;
        partyView.title = party.title; partyView.mapName = party.mapName; partyView.status = party.status; partyView.members = party.members;
        foreach (var member in party.members) if (member != null) FitName(member.playerName);
        var characters = party.members.Select(member => member != null ? member.appearance : null).ToArray();
        foreach (var member in party.members)
        {
            if (member == null) continue;
            if (member.appearance != null && !member.appearance.transform.IsChildOf(partyView.transform)) member.appearance = null;
            if (member.occupiedRoot != null && !member.occupiedRoot.transform.IsChildOf(partyView.transform)) member.occupiedRoot = null;
            if (member.emptyRoot != null && !member.emptyRoot.transform.IsChildOf(partyView.transform)) member.emptyRoot = null;
        }
        var partyBindings = new List<LobbyButtonBinding> { Bind(party.primaryButton, LobbyAction.Primary), Bind(party.leaveButton, LobbyAction.LeaveParty), Bind(party.returnButton, LobbyAction.Back) };
        AddUnboundButtons(partyView.transform, partyBindings, LobbyAction.Placeholder); partyView.buttons = Bindings(partyBindings.ToArray());

        var roomsView = Page<RoomListView>(party.roomPanel, LobbyPage.Rooms);
        roomsView.content = party.roomListContainer; roomsView.entryPrefab = party.roomEntryPrefab; roomsView.empty = party.emptyRooms;
        if (home.tmpStatus != null) roomsView.status = UnityEngine.Object.Instantiate(home.tmpStatus, roomsView.transform, true);
        roomsView.buttons = Bindings(Bind(party.closeRoomsButton, LobbyAction.Back), Bind(party.refreshRoomsButton, LobbyAction.RefreshRooms));

        // Bake the legacy runtime-generated custom-team UI once, then replace the old component with a passive view.
        typeof(LobbyMenuUI).GetMethod("BuildPvpTeamView", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(custom, null);
        var customView = Page<CustomRoomView>(custom.gameObject, LobbyPage.CustomRoom);
        customView.browsePanel = custom.panelBrowse; customView.roomPanel = custom.panelInRoom;
        customView.roomName = custom.inputRoomName; customView.localName = custom.inputPlayerName;
        customView.content = custom.roomListContainer; customView.rowPrefab = custom.roomEntryPrefab;
        customView.joinButton = custom.btnJoin; customView.createButton = custom.btnCreateRoom; customView.startButton = custom.btnStartGame;
        customView.empty = custom.txtEmpty; customView.roomTitle = custom.txtTitle; customView.memberList = custom.txtMemberList; customView.roomStatus = custom.txtStatus;
        customView.browseTitle = custom.panelBrowse.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(text => text.text.Contains("创建"));
        customView.teamPanel = Required(custom.panelInRoom.transform.Find("PVPTeamRoom"), "Baked team panel").gameObject;
        customView.redHeader = At<UnityEngine.UI.Text>(customView.teamPanel.transform, "RedTeam/RedTeamHeader");
        customView.blueHeader = At<UnityEngine.UI.Text>(customView.teamPanel.transform, "BlueTeam/BlueTeamHeader");
        customView.hint = At<UnityEngine.UI.Text>(customView.teamPanel.transform, "PvpHint");
        var customBindings = new List<LobbyButtonBinding> { Bind(custom.btnBack, LobbyAction.Back), Bind(custom.btnCreateRoom, LobbyAction.CreateRoom),
            Bind(custom.btnJoin, LobbyAction.JoinRoom), Bind(custom.btnRefresh, LobbyAction.RefreshCustomRooms), Bind(custom.btnStartGame, LobbyAction.StartRoom), Bind(custom.btnLeaveRoom, LobbyAction.LeaveRoom) };
        var slots = new List<CustomTeamSlot>();
        foreach (var button in customView.teamPanel.GetComponentsInChildren<UnityEngine.UI.Button>(true))
        {
            if (button.name.StartsWith("Character")) { customBindings.Add(Bind(button, LobbyAction.SelectCharacter)); continue; }
            var team = button.name.StartsWith("Red") ? LobbyTeam.Red : LobbyTeam.Blue;
            slots.Add(new CustomTeamSlot { button = button, background = button.GetComponent<UnityEngine.UI.Image>(), label = button.GetComponentInChildren<UnityEngine.UI.Text>(), team = team });
            customBindings.Add(Bind(button, team == LobbyTeam.Red ? LobbyAction.SelectRed : LobbyAction.SelectBlue));
        }
        customView.teamSlots = slots.ToArray(); customView.buttons = Bindings(customBindings.ToArray());

        var profile = Page<ProfilePanelView>(profileObject, LobbyPage.Profile);
        profile.overview = Required(profile.transform.Find("FirstPagePanel"), "Profile overview").gameObject;
        profile.information = Required(profile.transform.Find("InformationPanel"), "Profile information").gameObject;
        profile.playerName = At<TMP_Text>(profile.transform, "InformationPanel/Name/name");
        FitName(profile.playerName);
        profile.buttons = Bindings(Bind(At<UnityEngine.UI.Button>(profile.transform, "SideBar/Return"), LobbyAction.Back),
            Bind(At<UnityEngine.UI.Button>(profile.transform, "SideBar/FirstPage"), LobbyAction.ProfileOverview),
            Bind(At<UnityEngine.UI.Button>(profile.transform, "SideBar/Information"), LobbyAction.ProfileInformation),
            Bind(At<UnityEngine.UI.Button>(profile.transform, "InformationPanel/Avatar/avatar"), LobbyAction.Avatar));
        CreateAvatarPicker(profile, party.avatarCatalog);
        CreateProfileOverview(profile);

        var modalCanvas = OverlayCanvas("ModalCanvas", 500, new Vector2(1920, 1080));
        tip.transform.SetParent(modalCanvas.transform, false); exit.transform.SetParent(modalCanvas.transform, false);
        var message = Dialog(tip.gameObject, tip.btnNo, null);
        foreach (var button in tip.GetComponentsInChildren<UnityEngine.UI.Button>(true)) if (button != tip.btnNo) button.gameObject.SetActive(false);
        var confirm = Dialog(exit.gameObject, exit.btnYes, exit.btnNo);
        var wardrobe = CreateCustomization();
        var loading = CreateLoading();

        var avatars = party.avatarCatalog; var homeCharacter = party.homeCharacter;
        var rootObject = home.gameObject;
        foreach (var component in legacy) UnityEngine.Object.DestroyImmediate(component);
        rootObject.name = "UIRoot";
        var rootView = rootObject.AddComponent<LobbyUIRoot>();
        rootView.pages = new LobbyPageView[] { mainView, homeView, partyView, roomsView, customView, profile, wardrobe };
        rootView.partyView = partyView; rootView.roomsView = roomsView; rootView.customRoomView = customView;
        rootView.customizationView = wardrobe; rootView.messageDialog = message; rootView.confirmDialog = confirm;
        rootView.avatars = avatars; rootView.homeCharacter = homeCharacter; rootView.partyCharacters = characters; rootView.loading = loading;
        BindIdentity(rootView, homeCanvas);

        var mainEnvironment = new GameObject("MainMenuPresentation"); var lobbyEnvironment = new GameObject("LobbyPresentation");
        var mainCamera = FindBaseCamera(scene, mainCanvas.worldCamera); var lobbyCamera = FindBaseCamera(scene, homeCanvas.worldCamera);
        Reparent(mainCanvas.worldCamera.gameObject, mainEnvironment.transform); Reparent(homeCanvas.worldCamera.gameObject, lobbyEnvironment.transform);
        if (mainCamera != null) Reparent(mainCamera.gameObject, mainEnvironment.transform);
        if (lobbyCamera != null && lobbyCamera != mainCamera) Reparent(lobbyCamera.gameObject, lobbyEnvironment.transform);
        foreach (var canvas in scene.GetRootGameObjects().Select(item => item.GetComponent<Canvas>()).Where(item => item != null && item.name == "BackCanvas").ToArray())
            Reparent(canvas.gameObject, canvas.worldCamera == mainCamera ? mainEnvironment.transform : lobbyEnvironment.transform);
        rootView.mainEnvironment = new[] { mainEnvironment }; rootView.lobbyEnvironment = new[] { lobbyEnvironment };
        rootView.mainCharacter = scene.GetRootGameObjects().FirstOrDefault(item => item.name == "Lumine");
        if (rootView.mainCharacter != null) Reparent(rootView.mainCharacter, mainEnvironment.transform);
        rootView.mainDecorations = new[] { mainCanvas.transform.Find("GameTitle")?.gameObject, mainCanvas.transform.Find("Version")?.gameObject };
        foreach (var camera in scene.GetRootGameObjects().SelectMany(item => item.GetComponentsInChildren<Camera>(true)))
            camera.cullingMask &= ~(1 << 31); // The wardrobe owns the isolated preview layer.

        var ui = new GameObject("UI");
        Reparent(mainCanvas.gameObject, ui.transform); Reparent(homeCanvas.gameObject, ui.transform);
        Reparent(modalCanvas.gameObject, ui.transform); Reparent(wardrobe.gameObject, ui.transform); Reparent(loading, ui.transform);
        foreach (var view in rootView.pages) { view.gameObject.SetActive(false); Save(view.gameObject, view.page + "Panel"); }
        message.gameObject.SetActive(false); confirm.gameObject.SetActive(false); loading.SetActive(false);
        Save(message.gameObject, "MessageDialog"); Save(confirm.gameObject, "ConfirmDialog"); Save(loading, "LoadingOverlay");
        mainEnvironment.SetActive(true); lobbyEnvironment.SetActive(false); mainView.gameObject.SetActive(true);
        foreach (var character in characters) if (character != null) character.gameObject.SetActive(false);
        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Could not save migrated GameLobby.");
        AssetDatabase.SaveAssets();
        File.WriteAllText("tmp/LobbyMvc/migration.json", "{\"success\":true,\"backup\":\"" + backup.Replace("\\", "/") + "\",\"panelCount\":7,\"dialogCount\":2}");
        Debug.Log("[Lobby MVC] Migrated authored GameLobby to seven panel prefabs and two modal prefabs.");
    }

    private static T Find<T>(Scene scene) where T : Component => scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<T>(true)).FirstOrDefault();
    public static void FinalizeBindings()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Stop Play Mode first.");
        var scene = SceneManager.GetActiveScene(); var root = Required(Find<LobbyUIRoot>(scene), "MVC root");
        var home = root.pages.First(page => page.page == LobbyPage.Home);
        if (root.roomsView.status == null && home.status != null)
        { root.roomsView.status = UnityEngine.Object.Instantiate(home.status, root.roomsView.transform, true); root.roomsView.status.name = "RequestStatus"; Save(root.roomsView.gameObject, "RoomsPanel"); }
        var main = root.pages.First(page => page.page == LobbyPage.MainMenu); main.gameObject.SetActive(true);
        foreach (var binding in main.buttons)
        {
            if (binding.button.name == "CustomRoom-Btn") binding.action = LobbyAction.CustomPvp;
            if (binding.button.name == "TrainingRange-Btn") binding.action = LobbyAction.Placeholder;
        }
        Save(main.gameObject, "MainMenuPanel");
        FitName(home.playerName); Save(home.gameObject, "HomePanel");
        foreach (var member in root.partyView.members) if (member != null) FitName(member.playerName);
        Save(root.partyView.gameObject, "PartyPanel");
        var profile = (ProfilePanelView)root.pages.First(page => page.page == LobbyPage.Profile);
        FitName(profile.playerName); CreateProfileOverview(profile); Save(profile.gameObject, "ProfilePanel");
        if (root.mainCharacter == null)
        {
            root.mainCharacter = scene.GetRootGameObjects().FirstOrDefault(item => item.name == "Lumine");
            if (root.mainCharacter != null) Reparent(root.mainCharacter, root.mainEnvironment[0].transform);
        }
        BindIdentity(root, home.GetComponentInParent<Canvas>());
        EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene); AssetDatabase.SaveAssets();
    }
    private static T Required<T>(T value, string label) where T : UnityEngine.Object => value != null ? value : throw new InvalidOperationException("Missing authored " + label);
    private static void FitName(TMP_Text text)
    { if (text == null) return; text.enableWordWrapping = false; text.enableAutoSizing = true; text.fontSizeMax = text.fontSize; text.fontSizeMin = 18; text.overflowMode = TextOverflowModes.Ellipsis; }
    private static void BindIdentity(LobbyUIRoot root, Canvas canvas)
    {
        var target = canvas.transform.Find("UID"); if (target == null) return;
        root.identity = target.GetComponent<LobbyIdentityView>() ?? target.gameObject.AddComponent<LobbyIdentityView>();
        root.identity.uid = target.GetComponent<TMP_Text>(); root.identity.legacyUid = target.GetComponent<UnityEngine.UI.Text>(); FitName(root.identity.uid);
        if (root.identity.legacyUid != null)
        { root.identity.legacyUid.resizeTextForBestFit = true; root.identity.legacyUid.resizeTextMinSize = 12; root.identity.legacyUid.resizeTextMaxSize = root.identity.legacyUid.fontSize; }
        Save(target.gameObject, "LobbyIdentity");
    }
    private static T At<T>(Transform root, string path) where T : Component => root.Find(path)?.GetComponent<T>();
    private static void Unpack(GameObject item)
    {
        var outer = PrefabUtility.GetOutermostPrefabInstanceRoot(item);
        if (outer != null) PrefabUtility.UnpackPrefabInstance(outer, PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);
    }
    private static T Page<T>(GameObject item, LobbyPage page) where T : LobbyPageView
    { var view = item.AddComponent<T>(); view.page = page; item.GetComponent<CanvasGroup>().alpha = 1; return view; }
    private static LobbyButtonBinding Bind(UnityEngine.UI.Button button, LobbyAction action) => button != null ? new LobbyButtonBinding { button = button, action = action } : null;
    private static LobbyButtonBinding[] Bindings(params LobbyButtonBinding[] bindings) => bindings.Where(binding => binding != null && binding.button != null).ToArray();
    private static void AddUnboundButtons(Transform root, List<LobbyButtonBinding> bindings, LobbyAction action)
    { foreach (var button in root.GetComponentsInChildren<UnityEngine.UI.Button>(true)) if (!bindings.Any(binding => binding != null && binding.button == button)) bindings.Add(Bind(button, action)); }
    private static void Reparent(GameObject item, Transform parent) { if (item != null) item.transform.SetParent(parent, true); }
    private static void Save(GameObject item, string name)
    { PrefabUtility.SaveAsPrefabAssetAndConnect(item, Prefabs + "/" + name + ".prefab", InteractionMode.AutomatedAction); }
    private static Camera FindBaseCamera(Scene scene, Camera overlay)
    {
        foreach (var camera in scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Camera>(true)))
        {
            var data = camera.GetComponent<UniversalAdditionalCameraData>();
            if (data != null && data.renderType == CameraRenderType.Base && data.cameraStack.Contains(overlay)) return camera;
        }
        return null;
    }
    private static LobbyDialogView Dialog(GameObject item, UnityEngine.UI.Button yes, UnityEngine.UI.Button no)
    {
        var view = item.AddComponent<LobbyDialogView>(); view.confirmButton = yes; view.cancelButton = no;
        view.tmpMessage = item.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(text => text.GetComponentInParent<UnityEngine.UI.Button>() == null);
        view.message = item.GetComponentsInChildren<UnityEngine.UI.Text>(true).FirstOrDefault(text => text.GetComponentInParent<UnityEngine.UI.Button>() == null);
        var animator = item.GetComponent<Animator>(); if (animator != null) UnityEngine.Object.DestroyImmediate(animator);
        var group = item.GetComponent<CanvasGroup>(); if (group != null) { group.alpha = 1; group.interactable = group.blocksRaycasts = true; }
        if (view.tmpMessage != null) { view.tmpMessage.enableAutoSizing = true; view.tmpMessage.fontSizeMin = 18; view.tmpMessage.fontSizeMax = view.tmpMessage.fontSize; }
        if (view.message != null) { view.message.resizeTextForBestFit = true; view.message.resizeTextMinSize = 18; view.message.resizeTextMaxSize = view.message.fontSize; }
        return view;
    }
    private static Canvas OverlayCanvas(string name, int order, Vector2 resolution)
    {
        var item = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(UnityEngine.UI.CanvasScaler), typeof(UnityEngine.UI.GraphicRaycaster));
        var canvas = item.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = order;
        var scaler = item.GetComponent<UnityEngine.UI.CanvasScaler>(); scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = resolution; scaler.matchWidthOrHeight = 0.5f; return canvas;
    }
    private static Font FontAsset => AssetDatabase.LoadAssetAtPath<Font>("Assets/Resource/Fonts/优设标题黑_爱给网_aigei_com.ttf") ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
    private static UnityEngine.UI.Image Panel(Transform parent, string name, Vector2 min, Vector2 max, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(UnityEngine.UI.Image)); go.transform.SetParent(parent, false);
        var rect = go.GetComponent<RectTransform>(); rect.anchorMin = min; rect.anchorMax = max; rect.offsetMin = rect.offsetMax = Vector2.zero;
        var image = go.GetComponent<UnityEngine.UI.Image>(); image.color = color; image.raycastTarget = false; return image;
    }
    private static void Place(RectTransform rect, float x, float y, float width, float height)
    { rect.anchorMin = rect.anchorMax = new Vector2(0, 1); rect.pivot = new Vector2(0, 1); rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(width, height); }
    private static UnityEngine.UI.Text Label(Transform parent, string name, string text, float x, float y, float width, float height, int size, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(UnityEngine.UI.Text)); go.transform.SetParent(parent, false);
        var label = go.GetComponent<UnityEngine.UI.Text>(); label.font = FontAsset; label.fontSize = size; label.text = text; label.color = color;
        label.alignment = TextAnchor.MiddleLeft; label.raycastTarget = false; Place(label.rectTransform, x, y, width, height); return label;
    }
    private static UnityEngine.UI.Button Button(Transform parent, string name, string text, float x, float y, float width, float height)
    {
        var panel = Panel(parent, name, Vector2.zero, Vector2.zero, new Color(0.14f, 0.24f, 0.34f)); Place(panel.rectTransform, x, y, width, height); panel.raycastTarget = true;
        var button = panel.gameObject.AddComponent<UnityEngine.UI.Button>(); button.targetGraphic = panel;
        var label = Label(panel.transform, "Label", text, 0, 0, width, height, 20, Color.white); label.alignment = TextAnchor.MiddleCenter; return button;
    }
    private static CustomizationView CreateCustomization()
    {
        var canvas = OverlayCanvas("CustomizationPanel", 150, new Vector2(1600, 900));
        var view = Page<CustomizationView>(canvas.gameObject, LobbyPage.Customization);
        Panel(canvas.transform, "Background", Vector2.zero, Vector2.one, new Color(0.035f, 0.05f, 0.085f, 0.99f));
        Label(canvas.transform, "Title", "RIFLEGIRL / 角色与配装", 42, 40, 1050, 56, 30, new Color(0.88f, 0.94f, 1f));
        var back = Button(canvas.transform, "Back", "返回", 1360, 40, 196, 48);
        var left = Panel(canvas.transform, "Options", Vector2.zero, Vector2.zero, new Color(0.075f, 0.1f, 0.15f)); Place(left.rectTransform, 42, 118, 550, 682);
        Label(left.transform, "AppearanceTitle", "外观", 24, 18, 480, 40, 25, Color.white);
        string[] names = { "服装", "面部预设", "衣装配色", "发色", "帽子", "战斗配件", "主武器", "主武器瞄具", "手枪瞄具" };
        float[] ys = { 68, 126, 184, 242, 300, 358, 472, 530, 588 };
        view.values = new UnityEngine.UI.Text[9]; view.previous = new UnityEngine.UI.Button[9]; view.next = new UnityEngine.UI.Button[9];
        for (int i = 0; i < names.Length; i++)
        {
            Label(left.transform, "Choice" + i + "Title", names[i], 24, ys[i], 155, 42, 20, new Color(0.67f, 0.75f, 0.86f));
            view.values[i] = Label(left.transform, "Choice" + i + "Value", "", 212, ys[i], 224, 42, 20, Color.white); view.values[i].alignment = TextAnchor.MiddleCenter;
            view.previous[i] = Button(left.transform, "Choice" + i + "Previous", "‹", 168, ys[i], 40, 42);
            view.next[i] = Button(left.transform, "Choice" + i + "Next", "›", 455, ys[i], 40, 42);
        }
        Label(left.transform, "LoadoutTitle", "PVP 配装", 24, 426, 480, 38, 25, Color.white);
        var imageObject = new GameObject("CharacterPreview", typeof(RectTransform), typeof(UnityEngine.UI.RawImage)); imageObject.transform.SetParent(canvas.transform, false);
        var image = imageObject.GetComponent<UnityEngine.UI.RawImage>(); Place(image.rectTransform, 624, 118, 466, 682); image.color = Color.white;
        view.preview = imageObject.AddComponent<CharacterPreviewView>(); view.preview.image = image;
        view.preview.characterPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/Customization/RifleGirlBody.prefab");
        view.preview.idle = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>("Assets/Resources/Customization/RifleGirlLobby.controller");
        Label(canvas.transform, "RotateHint", "拖动旋转角色", 660, 754, 380, 30, 18, new Color(0.6f, 0.7f, 0.82f));
        var right = Panel(canvas.transform, "Account", Vector2.zero, Vector2.zero, new Color(0.075f, 0.1f, 0.15f)); Place(right.rectTransform, 1122, 118, 434, 682);
        Label(right.transform, "AccountTitle", "账号与云端档案", 24, 20, 384, 44, 25, Color.white);
        view.accountLabel = Label(right.transform, "AccountLabel", "", 24, 74, 380, 64, 20, new Color(0.62f, 0.8f, 0.94f));
        view.reloadButton = Button(right.transform, "Reload", "重新载入云端档案", 24, 155, 378, 42);
        Label(right.transform, "AccountHint", "注册、登录及切换账号请在 Login 界面进行。\n保存后，开局和重生使用此配装。", 24, 221, 378, 90, 17, new Color(0.62f, 0.7f, 0.81f));
        view.saveButton = Button(right.transform, "Save", "保存到账号", 24, 565, 378, 62); view.saveLabel = view.saveButton.GetComponentInChildren<UnityEngine.UI.Text>();
        view.message = Label(canvas.transform, "Status", "", 44, 823, 1278, 52, 20, new Color(0.64f, 0.82f, 0.98f));
        view.buttons = Bindings(Bind(back, LobbyAction.Back), Bind(view.saveButton, LobbyAction.SaveProfile), Bind(view.reloadButton, LobbyAction.ReloadProfile));
        return view;
    }
    private static GameObject CreateLoading()
    {
        var canvas = OverlayCanvas("LoadingOverlay", 600, new Vector2(1600, 900));
        var background = Panel(canvas.transform, "Background", Vector2.zero, Vector2.one, new Color(0.035f, 0.05f, 0.085f, 0.98f)); background.raycastTarget = true;
        var label = Label(canvas.transform, "Status", "正在读取用户数据…", 300, 390, 1000, 120, 36, Color.white); label.alignment = TextAnchor.MiddleCenter;
        return canvas.gameObject;
    }
    private static void CreateAvatarPicker(ProfilePanelView profile, PlayerAvatarCatalog catalog)
    {
        var current = Required(profile.transform.Find("InformationPanel/Avatar/avatar"), "Profile avatar");
        profile.avatar = current.GetComponent<PlayerAvatarView>() ?? current.gameObject.AddComponent<PlayerAvatarView>();
        profile.avatar.image = current.GetComponent<UnityEngine.UI.Image>(); profile.avatar.rawImage = current.GetComponent<UnityEngine.UI.RawImage>();
        var picker = Required(profile.transform.Find("InformationPanel/ReviseAvatarPanel"), "Avatar picker"); profile.avatarPicker = picker.gameObject;
        var background = picker.GetComponent<UnityEngine.UI.Image>() ?? picker.gameObject.AddComponent<UnityEngine.UI.Image>();
        background.color = new Color(0.035f, 0.05f, 0.085f, 0.98f); background.raycastTarget = true;
        Label(picker, "Title", "选择头像", 350, 45, 900, 80, 36, Color.white);
        var close = Button(picker, "Close", "返回个人资料", 1460, 65, 300, 60);
        var gridObject = new GameObject("AvatarOptions", typeof(RectTransform), typeof(UnityEngine.UI.GridLayoutGroup)); gridObject.transform.SetParent(picker, false);
        var rect = gridObject.GetComponent<RectTransform>(); rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = new Vector2(100, -20); rect.sizeDelta = new Vector2(1320, 680);
        var grid = gridObject.GetComponent<UnityEngine.UI.GridLayoutGroup>(); grid.cellSize = new Vector2(140, 140); grid.spacing = new Vector2(25, 25);
        grid.constraint = UnityEngine.UI.GridLayoutGroup.Constraint.FixedColumnCount; grid.constraintCount = 8;
        int count = catalog != null && catalog.avatars != null ? Mathf.Min(32, catalog.avatars.Length) : 0;
        profile.avatarChoices = new PlayerAvatarView[count]; profile.choiceButtons = new UnityEngine.UI.Button[count];
        for (int i = 0; i < count; i++)
        {
            var option = UnityEngine.Object.Instantiate(current.gameObject, gridObject.transform, false); option.name = "Avatar" + i;
            var button = option.GetComponent<UnityEngine.UI.Button>(); button.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            var avatar = option.GetComponent<PlayerAvatarView>(); avatar.image = option.GetComponent<UnityEngine.UI.Image>(); avatar.rawImage = option.GetComponent<UnityEngine.UI.RawImage>();
            avatar.Show((byte)i, catalog); profile.avatarChoices[i] = avatar; profile.choiceButtons[i] = button;
        }
        profile.buttons = profile.buttons.Concat(new[] { Bind(close, LobbyAction.CloseAvatarPicker) }).ToArray();
        picker.gameObject.SetActive(false);
    }
    private static void CreateProfileOverview(ProfilePanelView profile)
    {
        if (profile.overviewName != null || profile.overview.transform.childCount > 0) return;
        var name = UnityEngine.Object.Instantiate(profile.information.transform.Find("Name").gameObject, profile.overview.transform, false);
        name.name = "PlayerName"; profile.overviewName = At<TMP_Text>(name.transform, "name"); FitName(profile.overviewName);
        var title = At<TMP_Text>(name.transform, "Title"); if (title != null) title.text = "玩家";
        var uid = UnityEngine.Object.Instantiate(profile.information.transform.Find("Sex").gameObject, profile.overview.transform, false); uid.name = "UID";
        title = At<TMP_Text>(uid.transform, "Title"); if (title != null) title.text = "UID";
        profile.overviewUid = UnityEngine.Object.Instantiate(profile.overviewName, uid.transform, false); FitName(profile.overviewUid);
        var avatar = UnityEngine.Object.Instantiate(profile.information.transform.Find("Avatar").gameObject, profile.overview.transform, false); avatar.name = "Avatar";
        profile.overviewAvatar = avatar.GetComponentInChildren<PlayerAvatarView>(true);
        foreach (var button in avatar.GetComponentsInChildren<UnityEngine.UI.Button>(true)) UnityEngine.Object.DestroyImmediate(button);
    }
}
