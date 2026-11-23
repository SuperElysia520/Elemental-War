using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FPS.UI;
using FPS.Settings;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TextCore.LowLevel;
using Object = UnityEngine.Object;

/// <summary>Builds the approved blue/white homepage as authored, editable Unity assets.</summary>
public static class UnifiedLobbyBuilder
{
    public const string Folder = "Assets/Resource/UI/UnifiedLobby";
    public const string PrefabPath = Folder + "/UnifiedHome.prefab";
    public const string Output = UnifiedLobbyEditorTasks.Output;
    private static readonly Color Blue = C("166FCB"), Ink = C("193B69"), Muted = C("647E9C"), Yellow = C("FFE34C");
    private static TMP_FontAsset font;
    private static readonly Dictionary<string, Material> materials = new Dictionary<string, Material>();
    private static readonly List<LobbyButtonBinding> bindings = new List<LobbyButtonBinding>();

    [MenuItem("Tools/FPS/Lobby/Apply Unified Blue White Homepage")]
    public static void Apply()
    {
        Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Stop Play Mode before changing the authored lobby.");
        var scene = SceneManager.GetActiveScene();
        Require(scene.path == "Assets/Scenes/GameLobby.unity", "Open GameLobby before applying the design.");
        var root = FindRoot(); Require(root != null, "The existing MVC root is missing.");
        Require(root.homeView == null, "Unified homepage already exists. It can be edited directly as a prefab.");
        Directory.CreateDirectory(Output + "/before");
        Require(EditorSceneManager.SaveScene(scene, Path.GetFullPath(Output + "/before/GameLobby-before-apply.unity"), true), "Could not back up the open scene.");
        foreach (var page in root.pages)
        {
            var path = AssetDatabase.GetAssetPath(PrefabUtility.GetCorrespondingObjectFromSource(page));
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Copy(path, Output + "/before/" + Path.GetFileName(path), true);
        }
        Directory.CreateDirectory(Folder + "/Materials"); Directory.CreateDirectory(Folder + "/Fonts"); AssetDatabase.Refresh();
        EnsureFont();
        var previous = root.pages.Where(p => p.page == LobbyPage.MainMenu || p.page == LobbyPage.Home || p.page == LobbyPage.Party).ToArray();
        var character = root.homeCharacter;
        Require(character != null && character.GetComponentInChildren<RifleGirlAppearance>(true) != null, "The authored RifleGirl preview is missing.");
        var sourceInstance = PrefabUtility.GetOutermostPrefabInstanceRoot(character);
        if(sourceInstance != null) PrefabUtility.UnpackPrefabInstance(sourceInstance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        RifleGirlAssetBuilder.ConfigureLobbyAppearance(character);
        var environment = BuildEnvironment(character, out Camera camera);
        root.unifiedEnvironment = environment; root.homeCharacter = character;
        var map = CaptureMap(camera, character);
        var home = BuildHome(root.avatars, map);
        root.homeView = home; root.partyView = null;
        root.pages = new LobbyPageView[] { home }.Concat(root.pages.Except(previous)).ToArray();
        foreach (var item in previous) Object.DestroyImmediate(item.gameObject);
        foreach (var item in root.partyCharacters) if (item != null) item.gameObject.SetActive(false);
        root.partyCharacters = new RifleGirlAppearance[0];
        foreach (var page in root.pages)
        {
            var canvas = page.GetComponentInParent<Canvas>(true);
            if (canvas != null && page != home)
            {
                canvas.gameObject.SetActive(true); canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.overrideSorting = true; canvas.sortingOrder = 100;
            }
            page.Show(page == home);
        }
        foreach (var dialog in new[] { root.messageDialog, root.confirmDialog })
        {
            var canvas = dialog.GetComponentInParent<Canvas>(true);
            if (canvas != null) { canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.overrideSorting = true; canvas.sortingOrder = 500; canvas.gameObject.SetActive(true); }
            dialog.Hide();
        }
        foreach (var item in root.mainEnvironment.Concat(root.lobbyEnvironment)) if (item != null) item.SetActive(false);
        if (root.identity != null) root.identity.gameObject.SetActive(false);
        if (root.mainCharacter != null) root.mainCharacter.SetActive(false);
        foreach (var item in root.mainDecorations) if (item != null) item.SetActive(false);
        Preview(home, root.avatars);
        PrefabUtility.SaveAsPrefabAssetAndConnect(environment, Folder + "/TrainingStation.prefab", InteractionMode.AutomatedAction);
        PrefabUtility.SaveAsPrefabAssetAndConnect(home.gameObject, PrefabPath, InteractionMode.AutomatedAction);
        EditorUtility.SetDirty(root); EditorSceneManager.MarkSceneDirty(scene);
        Require(EditorSceneManager.SaveScene(scene), "Could not save the unified lobby.");
        AssetDatabase.SaveAssets(); Refine();
        Selection.activeGameObject = home.gameObject;
        Debug.Log("[Unified Lobby] Applied the approved A design; one homepage, no single-player entry.");
    }

    private static void EnsureFont()
    {
        string path = Folder + "/Fonts/UnifiedLobbyBody.asset";
        font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
        if (font != null) return;
        var source = AssetDatabase.LoadAssetAtPath<Font>("Assets/Resource/UI/Fonts/锐字云字库粗圆体GBK_爱给网_aigei_com.ttf");
        Require(source != null, "The project Chinese sans-serif font is missing.");
        font = TMP_FontAsset.CreateFontAsset(source, 64, 6, GlyphRenderMode.SDFAA, 2048, 2048, AtlasPopulationMode.Dynamic, true);
        font.name = "UnifiedLobbyBody"; font.atlasPopulationMode = AtlasPopulationMode.Dynamic;
        var fallback = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/Resource/UI/Fonts/Mvc/LobbyCjkFallback.asset");
        font.fallbackFontAssetTable = new List<TMP_FontAsset>();
        if (fallback != null) font.fallbackFontAssetTable.Add(fallback);
        AssetDatabase.CreateAsset(font, path);
        foreach (var texture in font.atlasTextures) { texture.name = "UnifiedLobbySans Atlas"; AssetDatabase.AddObjectToAsset(texture, font); }
        font.material.name = "UnifiedLobbySans Material"; AssetDatabase.AddObjectToAsset(font.material, font);
        font.TryAddCharacters("作战大厅初始旅行者团队对抗自定义房间训练靶场选择模式角色服装武器队伍开始匹配取消等待员准备正在进入局解散离开长我已未测试地图世界频道邮件好友设置退出邀请创建处理请求同步账号失败请稍后重试暂无刷新保存返回1234567890ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz /:·…+-", out _);
        EditorUtility.SetDirty(font); AssetDatabase.SaveAssets();
    }

    public static void RepairCharacter()
    {
        Require(!EditorApplication.isPlayingOrWillChangePlaymode,"Stop Play Mode before restoring the preview.");
        var root=FindRoot();var scene=root.gameObject.scene;
        Require(root.homeView!=null&&root.unifiedEnvironment!=null,"The unified scene must already be authored.");
        const string recovery=Folder+"/CharacterRecovery.unity";
        File.Copy(Output+"/before/GameLobby-before-apply.unity",recovery,true);
        AssetDatabase.ImportAsset(recovery,ImportAssetOptions.ForceSynchronousImport);
        var backup=EditorSceneManager.OpenScene(recovery,OpenSceneMode.Additive);
        GameObject body;
        try
        {
            var original=backup.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<LobbyUIRoot>(true)).First();
            body=Object.Instantiate(original.homeCharacter);body.name="HomepageRifleGirl";
            if(PrefabUtility.IsPartOfPrefabInstance(body))PrefabUtility.UnpackPrefabInstance(PrefabUtility.GetOutermostPrefabInstanceRoot(body),PrefabUnpackMode.Completely,InteractionMode.AutomatedAction);
            SceneManager.MoveGameObjectToScene(body,scene);
        }
        finally{EditorSceneManager.CloseScene(backup,true);AssetDatabase.DeleteAsset(recovery);}
        var stationInstance=PrefabUtility.GetOutermostPrefabInstanceRoot(root.unifiedEnvironment);
        if(stationInstance!=null)PrefabUtility.UnpackPrefabInstance(stationInstance,PrefabUnpackMode.Completely,InteractionMode.AutomatedAction);
        body.transform.SetParent(root.unifiedEnvironment.transform,false);body.transform.localPosition=Vector3.zero;body.transform.localRotation=Quaternion.Euler(0,180,0);body.transform.localScale=Vector3.one;
        body.SetActive(true);SetLayer(body.transform,30);
        var animator=body.GetComponentInChildren<Animator>();if(animator!=null){animator.applyRootMotion=false;animator.Rebind();animator.Update(0);animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;}
        RifleGirlAssetBuilder.ConfigureLobbyAppearance(body);
        root.homeCharacter=body;
        PrefabUtility.SaveAsPrefabAssetAndConnect(root.unifiedEnvironment,Folder+"/TrainingStation.prefab",InteractionMode.AutomatedAction);
        EditorUtility.SetDirty(root);EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);AssetDatabase.SaveAssets();
        Validate();Capture();
    }

    public static void Refine()
    {
        Require(!EditorApplication.isPlayingOrWillChangePlaymode,"Stop Play Mode before refining the authored assets.");
        var root=FindRoot();var home=root.homeView;
        string fontPath=Folder+"/Fonts/UnifiedLobbyBody.asset";
        var bodyFont=AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(fontPath);
        if(bodyFont==null)
        {
            var source=AssetDatabase.LoadAssetAtPath<Font>("Assets/Resource/UI/Fonts/锐字云字库粗圆体GBK_爱给网_aigei_com.ttf");
            bodyFont=TMP_FontAsset.CreateFontAsset(source,64,6,GlyphRenderMode.SDFAA,2048,2048,AtlasPopulationMode.Dynamic,true);
            bodyFont.name="UnifiedLobbyBody";bodyFont.fallbackFontAssetTable=new List<TMP_FontAsset>();
            AssetDatabase.CreateAsset(bodyFont,fontPath);
            foreach(var texture in bodyFont.atlasTextures)AssetDatabase.AddObjectToAsset(texture,bodyFont);
            AssetDatabase.AddObjectToAsset(bodyFont.material,bodyFont);
        }
        foreach(var graphic in home.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
        {
            if(graphic.GetComponent<CanvasRenderer>()==null)graphic.gameObject.AddComponent<CanvasRenderer>();
            graphic.enabled=false;graphic.enabled=true;graphic.SetAllDirty();
        }
        foreach(var text in home.GetComponentsInChildren<TMP_Text>(true)){text.font=bodyFont;text.SetAllDirty();}
        var character=root.homeCharacter;var pose=character.GetComponent<UnifiedLobbyWeaponPose>()??character.AddComponent<UnifiedLobbyWeaponPose>();
        pose.animator=character.GetComponent<Animator>();
        pose.riflePrefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/PvpWeapons/Models/AssaultRifle.prefab");
        pose.sniperPrefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/PvpWeapons/Models/SniperRifle.prefab");
        if(pose.sniperPrefab==null)pose.sniperPrefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/PvpWeapons/Models/Sniper.prefab");
        if(pose.weapon==null)
        {
            var weapon=(GameObject)PrefabUtility.InstantiatePrefab(pose.riflePrefab,character.transform);weapon.name="LobbyWeapon";
            pose.weapon=weapon.GetComponent<PvpWeaponModel>();pose.weapon.SelectSight(0);SetLayer(weapon.transform,30);
        }
        string controllerPath=Folder+"/HomepageIdle.controller";
        if(AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath)==null)AssetDatabase.CopyAsset("Assets/Resources/Customization/RifleGirlLobby.controller",controllerPath);
        var controller=AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);var layers=controller.layers;
        for(int i=0;i<layers.Length;i++)layers[i].iKPass=true;controller.layers=layers;EditorUtility.SetDirty(controller);
        pose.animator.runtimeAnimatorController=controller;pose.animator.Rebind();pose.animator.Update(.01f);
        var camera=root.unifiedEnvironment.GetComponentInChildren<Camera>(true);camera.transform.LookAt(new Vector3(.22f,.89f,0));
        camera.tag="MainCamera";
        var sun=root.unifiedEnvironment.transform.Find("StationSun").GetComponent<Light>();sun.intensity=1.0f;
        root.unifiedEnvironment.transform.Find("StationFill").GetComponent<Light>().intensity=.2f;
        PrefabUtility.ApplyPrefabInstance(root.unifiedEnvironment,InteractionMode.AutomatedAction);
        PrefabUtility.ApplyPrefabInstance(home.gameObject,InteractionMode.AutomatedAction);
        EditorSceneManager.MarkSceneDirty(root.gameObject.scene);EditorSceneManager.SaveScene(root.gameObject.scene);AssetDatabase.SaveAssets();
        Validate();Capture();
    }

    private static UnifiedLobbyView BuildHome(PlayerAvatarCatalog catalog, Texture map)
    {
        bindings.Clear();
        var go = new GameObject("UnifiedHome", typeof(RectTransform), typeof(Canvas), typeof(UnityEngine.UI.CanvasScaler), typeof(UnityEngine.UI.GraphicRaycaster), typeof(CanvasGroup), typeof(UnifiedLobbyView), typeof(UnifiedLobbyLayout));
        var canvas = go.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 20;
        var scaler = go.GetComponent<UnityEngine.UI.CanvasScaler>(); scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920,1080); scaler.matchWidthOrHeight = .5f;
        var stage = R("Stage", go.transform, 0, 0, 1920, 1080);
        stage.anchorMin = stage.anchorMax = new Vector2(.5f,.5f); stage.pivot = new Vector2(.5f,.5f); stage.anchoredPosition = Vector2.zero;
        go.GetComponent<UnifiedLobbyLayout>().stage = stage;
        var view = go.GetComponent<UnifiedLobbyView>(); view.page = LobbyPage.Home;

        var header = Shape("Header",stage,0,0,1920,88,C("16579D"),0);
        header.gradient = true; header.endColor = C("2B86DC");
        Shape("HeaderRule",stage,0,87,1920,2,new Color(1,1,1,.85f),0);
        var account = Button("Profile",stage,"",28,10,350,68,Color.clear,LobbyAction.Profile);
        var portrait = R("Avatar",account.transform,8,0,64,64);
        var portraitImage = portrait.gameObject.AddComponent<UnityEngine.UI.Image>(); portraitImage.raycastTarget = false;
        view.avatar = portrait.gameObject.AddComponent<PlayerAvatarView>(); view.avatar.image = portraitImage;
        if(catalog != null) view.avatar.Show(0,catalog);
        view.playerName = T("Nickname",account.transform,"初始旅行者",92,2,250,36,32,Color.white);
        view.uid = T("UID",account.transform,"UID 111100001",92,40,250,23,17,new Color(.9f,.96f,1));
        Shape("Slash",stage,405,22,9,40,Yellow,0).rectTransform.localRotation=Quaternion.Euler(0,0,-20);
        T("Title",stage,"作战大厅",446,13,290,59,48,Color.white);
        T("Subtitle",stage,"FPS / ONLINE",734,34,240,26,19,new Color(.75f,.87f,1));
        IconButton("Mail",stage,UnifiedLobbyIcon.Symbol.Mail,1608,16,LobbyAction.Placeholder,"邮件");
        IconButton("Friends",stage,UnifiedLobbyIcon.Symbol.Team,1708,16,LobbyAction.Friends,"好友");
        IconButton("Settings",stage,UnifiedLobbyIcon.Symbol.Settings,1808,16,LobbyAction.Settings,"设置");

        T("ModeCaption",stage,"选择作战模式",44,200,340,30,18,Blue);
        Mode(stage,"TeamMode","团队对抗",UnifiedLobbyIcon.Symbol.Team,244,Yellow,LobbyAction.Multiplayer);
        Mode(stage,"CustomMode","自定义房间",UnifiedLobbyIcon.Symbol.Rooms,374,new Color(.97f,.99f,1,.94f),LobbyAction.CustomPvp);
        Mode(stage,"TrainingMode","训练靶场",UnifiedLobbyIcon.Symbol.Crosshair,504,new Color(.97f,.99f,1,.94f),LobbyAction.Training);
        var exit = Button("Exit",stage,"退出游戏",46,690,210,44,new Color(1,1,1,.66f),LobbyAction.Exit,18);
        Icon(exit.transform,UnifiedLobbyIcon.Symbol.Exit,12,8,28,28,Ink);
        Place(exit.GetComponentInChildren<TMP_Text>().rectTransform,48,0,150,44);

        var operations = Shape("Operations",stage,1320,154,560,810,new Color(.98f,.992f,1,.96f),18).rectTransform;
        var rim=Shape("Border",operations,0,0,560,810,new Color(1,1,1,.95f),18); rim.outline=true;rim.stroke=2;
        Shape("TeamRibbon",operations,0,0,288,56,Blue,16);
        view.teamCount = T("TeamCount",operations,"队伍  1/5",29,6,245,44,30,Color.white);
        view.memberAvatars = new PlayerAvatarView[5]; view.occupiedSlots = new GameObject[5]; view.emptySlots = new GameObject[5];
        view.memberNames = new TMP_Text[5];view.memberStates = new TMP_Text[5];
        for(int i=0;i<5;i++)
        {
            float x=34+i*100;
            var slot=Button("Member"+(i+1),operations,"",x,88,88,88,C("E4EDF8"),LobbyAction.CreateParty);
            var occupied=R("Occupied",slot.transform,4,4,80,80);
            var image=occupied.gameObject.AddComponent<UnityEngine.UI.Image>(); image.raycastTarget=false;image.preserveAspect=true;
            var avatar=occupied.gameObject.AddComponent<PlayerAvatarView>();avatar.image=image;
            var empty=T("Empty",slot.transform,"+",0,-1,88,88,56,C("8298B6"));empty.alignment=TextAlignmentOptions.Center;
            view.memberAvatars[i]=avatar;view.occupiedSlots[i]=occupied.gameObject;view.emptySlots[i]=empty.gameObject;
            view.memberNames[i]=T("MemberName"+(i+1),operations,"",x-4,185,96,24,13,Ink);view.memberNames[i].alignment=TextAlignmentOptions.Center;
            view.memberStates[i]=T("MemberState"+(i+1),operations,"",x-4,210,96,22,13,Blue);view.memberStates[i].alignment=TextAlignmentOptions.Center;
        }
        Shape("Divider",operations,30,248,500,1,C("AEC8E4"),0);
        T("ModeTitle",operations,"团队对抗",30,270,350,54,43,Ink);
        var badge=Shape("ModeBadge",operations,425,280,102,38,Blue,10);
        T("FiveVsFive",badge.transform,"5V5",0,0,102,38,27,Color.white).alignment=TextAlignmentOptions.Center;
        var mapRect=R("MapPreview",operations,30,344,500,180);
        var mapImage=mapRect.gameObject.AddComponent<UnityEngine.UI.RawImage>();mapImage.texture=map;mapImage.raycastTarget=false;
        view.mapName=T("MapName",operations,"团队竞技 · 5V5",30,535,500,28,20,Muted);
        view.roomsButton=Button("RoomList",operations,"房间列表",30,583,500,58,new Color(.93f,.97f,1),LobbyAction.Rooms,25);
        Shape("RoomBorder",view.roomsButton.transform,0,0,500,58,C("287CD2"),9,true);
        Icon(view.roomsButton.transform,UnifiedLobbyIcon.Symbol.Rooms,24,14,30,30,Blue);
        Icon(view.roomsButton.transform,UnifiedLobbyIcon.Symbol.Chevron,452,16,22,26,Blue);
        view.primaryButton=Button("StartMatching",operations,"开始匹配",30,666,500,105,Yellow,LobbyAction.Primary,43);
        view.primaryLabel=view.primaryButton.GetComponentInChildren<TMP_Text>(); Place(view.primaryLabel.rectTransform,90,0,380,105);
        Icon(view.primaryButton.transform,UnifiedLobbyIcon.Symbol.Crosshair,32,29,48,48,Ink);
        view.leaveButton=Button("LeaveParty",operations,"解散队伍",322,777,205,27,Color.clear,LobbyAction.LeaveParty,16);
        view.leaveLabel=view.leaveButton.GetComponentInChildren<TMP_Text>();view.leaveLabel.color=Muted;

        Bottom(stage,"Clothes","服装",UnifiedLobbyIcon.Symbol.Shirt,730,LobbyAction.Clothes);
        Bottom(stage,"Weapons","武器",UnifiedLobbyIcon.Symbol.Rifle,974,LobbyAction.Loadout);
        var chat=Button("Chat",stage,"聊天",36,1006,442,48,new Color(.1f,.29f,.52f,.65f),LobbyAction.Chat,20);
        chat.GetComponentInChildren<TMP_Text>().color=Color.white;
        Icon(chat.transform,UnifiedLobbyIcon.Symbol.Chat,14,8,32,32,Color.white);
        Place(chat.GetComponentInChildren<TMP_Text>().rectTransform,66,0,360,48);chat.GetComponentInChildren<TMP_Text>().alignment=TextAlignmentOptions.Left;
        view.status=T("Status",stage,"",498,914,790,42,21,Ink);view.status.alignment=TextAlignmentOptions.Center;
        view.buttons=bindings.ToArray();
        SplitRightColumn(view);
        SetLayer(go.transform,5);
        return view;
    }

    private static void Mode(Transform stage,string name,string label,UnifiedLobbyIcon.Symbol icon,float y,Color fill,LobbyAction action)
    {
        var button=Button(name,stage,label,36,y,408,106,fill,action,35);
        Icon(button.transform,icon,30,29,48,48,Ink);
        Place(button.GetComponentInChildren<TMP_Text>().rectTransform,100,0,280,106);
        button.GetComponentInChildren<TMP_Text>().alignment=TextAlignmentOptions.Left;
        if(action==LobbyAction.Multiplayer)Icon(button.transform,UnifiedLobbyIcon.Symbol.Chevron,365,35,22,34,Ink);
    }

    [MenuItem("Tools/FPS/Lobby/Split Squad and Match Cards")]
    public static void SplitCards()
    {
        Require(!EditorApplication.isPlayingOrWillChangePlaymode,"Stop Play Mode before changing lobby cards.");
        var root=FindRoot();Require(root!=null&&root.homeView!=null,"Open the unified GameLobby scene first.");
        var view=root.homeView;var scene=root.gameObject.scene;
        string backup=Output+"/before/split-cards-"+DateTime.Now.ToString("yyyyMMdd-HHmmss");
        Directory.CreateDirectory(backup);
        Require(EditorSceneManager.SaveScene(scene,Path.GetFullPath(backup+"/GameLobby.unity"),true),"Could not back up the current scene.");
        File.Copy(PrefabPath,backup+"/UnifiedHome.prefab");
        // Keep the current scene's authored overrides and all existing button/component identities.
        var instance=PrefabUtility.GetOutermostPrefabInstanceRoot(view.gameObject);
        if(instance!=null)PrefabUtility.UnpackPrefabInstance(instance,PrefabUnpackMode.OutermostRoot,InteractionMode.AutomatedAction);
        SplitRightColumn(view);
        Preview(view,root.avatars);
        PrefabUtility.SaveAsPrefabAssetAndConnect(view.gameObject,PrefabPath,InteractionMode.AutomatedAction);
        Save();Capture();
        // Preview capture restores its camera and canvas properties before the final save.
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[Unified Lobby] Cards saved with automatic stacking; the match card moves up while the squad is hidden.");
    }
    private static void SplitRightColumn(UnifiedLobbyView view)
    {
        if(view.squadCard!=null&&view.matchCard!=null){ArrangeRightColumn(view);return;}
        var column=(RectTransform)view.transform.Find("Stage/Operations");
        Require(column!=null,"The existing right column is missing.");
        var background=column.GetComponent<SettingsShape>();if(background!=null)background.enabled=false;
        var oldBorder=column.Find("Border");if(oldBorder!=null)oldBorder.gameObject.SetActive(false);
        var squad=Shape("SquadCard",column,0,0,560,260,new Color(.98f,.992f,1,.96f),18).rectTransform;
        var match=Shape("MatchCard",column,0,284,560,534,new Color(.98f,.992f,1,.96f),18).rectTransform;
        Shape("Border",squad,0,0,560,260,new Color(1,1,1,.95f),18,true);
        Shape("Border",match,0,0,560,534,new Color(1,1,1,.95f),18,true);
        MoveCardChild(column.Find("TeamRibbon"),squad,0);
        MoveCardChild(view.teamCount.transform,squad,0);
        MoveCardChild(column.Find("Divider"),squad,0);
        for(int i=0;i<view.memberAvatars.Length;i++)
        {
            MoveCardChild(view.memberAvatars[i].transform.parent,squad,0);
            MoveCardChild(view.memberNames[i].transform,squad,0);
            MoveCardChild(view.memberStates[i].transform,squad,0);
        }
        foreach(string name in new[]{"ModeTitle","ModeBadge","MapPreview"})MoveCardChild(column.Find(name),match,258);
        MoveCardChild(view.mapName.transform,match,258);
        MoveCardChild(view.roomsButton.transform,match,258);
        MoveCardChild(view.primaryButton.transform,match,258);
        MoveCardChild(view.leaveButton.transform,squad,0);
        Place((RectTransform)view.leaveButton.transform,322,12,205,32);
        Place(view.leaveLabel.rectTransform,6,0,193,32);view.leaveLabel.color=Blue;

        var create=Object.Instantiate(view.roomsButton.gameObject,match,false);create.name="CreateParty";
        view.createPartyButton=create.GetComponent<UnityEngine.UI.Button>();
        ConfigureSecondary(view.roomsButton,288,"房间列表");
        ConfigureSecondary(view.createPartyButton,30,"创建队伍");
        var teamIcon=create.GetComponentInChildren<UnifiedLobbyIcon>();if(teamIcon!=null)teamIcon.symbol=UnifiedLobbyIcon.Symbol.Team;
        view.buttons=new[]{new LobbyButtonBinding{button=view.createPartyButton,action=LobbyAction.CreateParty}}.Concat(view.buttons).ToArray();
        view.squadCard=squad.gameObject;view.matchCard=match.gameObject;
        squad.gameObject.SetActive(false);
        SetLayer(squad,5);SetLayer(match,5);
        ArrangeRightColumn(view);
    }
    private static void ArrangeRightColumn(UnifiedLobbyView view)
    {
        var column=(RectTransform)view.matchCard.transform.parent;
        Require(view.squadCard.transform.parent==column,"Both cards must belong to the same right column.");
        var layout=column.GetComponent<UnityEngine.UI.VerticalLayoutGroup>()??column.gameObject.AddComponent<UnityEngine.UI.VerticalLayoutGroup>();
        layout.childAlignment=TextAnchor.UpperLeft;layout.padding=new RectOffset();layout.spacing=24;
        layout.childControlWidth=false;layout.childControlHeight=false;
        layout.childForceExpandWidth=false;layout.childForceExpandHeight=false;
        UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(column);
    }
    private static void MoveCardChild(Transform child,RectTransform parent,float topOffset)
    {
        Require(child is RectTransform,"An expected card control is missing.");
        var rect=(RectTransform)child;var position=rect.anchoredPosition;var size=rect.sizeDelta;
        rect.SetParent(parent,false);Place(rect,position.x,-position.y-topOffset,size.x,size.y);
    }
    private static void ConfigureSecondary(UnityEngine.UI.Button button,float x,string caption)
    {
        Place((RectTransform)button.transform,x,325,242,58);
        var label=button.GetComponentInChildren<TMP_Text>(true);label.text=caption;label.fontSizeMax=label.fontSize=22;
        Place(label.rectTransform,46,0,186,58);
        var border=button.transform.Find("RoomBorder") as RectTransform;if(border!=null)Place(border,0,0,242,58);
        foreach(var icon in button.GetComponentsInChildren<UnifiedLobbyIcon>(true))
        {
            if(icon.symbol==UnifiedLobbyIcon.Symbol.Chevron)icon.gameObject.SetActive(false);
            else Place(icon.rectTransform,14,17,26,26);
        }
    }
    private static void Bottom(Transform stage,string name,string label,UnifiedLobbyIcon.Symbol icon,float x,LobbyAction action)
    {
        var button=Button(name,stage,label,x,970,216,64,new Color(.98f,.995f,1,.96f),action,27);
        Shape("Accent",button.transform,0,0,4,64,Yellow,0);
        Icon(button.transform,icon,25,14,38,38,Blue);
        Place(button.GetComponentInChildren<TMP_Text>().rectTransform,80,0,120,64);
    }
    private static void IconButton(string name,Transform parent,UnifiedLobbyIcon.Symbol icon,float x,float y,LobbyAction action,string label)
    {
        var button=Button(name,parent,"",x,y,78,60,Color.clear,action);
        Icon(button.transform,icon,19,3,40,40,Color.white);
        T("Caption",button.transform,label,0,44,78,18,12,new Color(.85f,.94f,1)).alignment=TextAlignmentOptions.Center;
    }
    private static UnityEngine.UI.Button Button(string name,Transform parent,string text,float x,float y,float w,float h,Color fill,LobbyAction action,int size=24)
    {
        var shape=Shape(name,parent,x,y,w,h,fill,Mathf.Min(16,h*.18f));shape.raycastTarget=true;
        var button=shape.gameObject.AddComponent<UnityEngine.UI.Button>();button.targetGraphic=shape;
        var colors=button.colors;colors.normalColor=Color.white;colors.highlightedColor=new Color(.86f,.94f,1);colors.pressedColor=new Color(.70f,.84f,1);colors.selectedColor=Color.white;colors.disabledColor=new Color(.75f,.80f,.86f,.65f);colors.fadeDuration=.1f;button.colors=colors;
        var navigation=button.navigation;navigation.mode=UnityEngine.UI.Navigation.Mode.None;button.navigation=navigation;
        if(!string.IsNullOrEmpty(text))T("Label",button.transform,text,6,0,w-12,h,size,Ink).alignment=TextAlignmentOptions.Center;
        bindings.Add(new LobbyButtonBinding{button=button,action=action});return button;
    }
    private static void Icon(Transform parent,UnifiedLobbyIcon.Symbol symbol,float x,float y,float w,float h,Color color)
    {var rect=R(symbol+"Icon",parent,x,y,w,h);var icon=rect.gameObject.AddComponent<UnifiedLobbyIcon>();icon.symbol=symbol;icon.color=color;icon.raycastTarget=false;}
    private static SettingsShape Shape(string name,Transform parent,float x,float y,float w,float h,Color color,float bevel,bool outline=false)
    {var rect=R(name,parent,x,y,w,h);var graphic=rect.gameObject.AddComponent<SettingsShape>();graphic.shape=SettingsShape.ShapeKind.Panel;graphic.bevel=bevel;graphic.color=color;graphic.raycastTarget=false;graphic.outline=outline;graphic.stroke=1;return graphic;}
    private static TMP_Text T(string name,Transform parent,string text,float x,float y,float w,float h,int size,Color color)
    {
        var rect=R(name,parent,x,y,w,h);var t=rect.gameObject.AddComponent<TextMeshProUGUI>();t.font=font;t.text=text;t.fontSize=size;t.color=color;
        t.alignment=TextAlignmentOptions.Left;t.enableWordWrapping=false;t.overflowMode=TextOverflowModes.Ellipsis;t.raycastTarget=false;
        t.enableAutoSizing=true;t.fontSizeMin=Mathf.Min(size,12);t.fontSizeMax=size;return t;
    }
    private static RectTransform R(string name,Transform parent,float x,float y,float w,float h)
    {var rect=new GameObject(name,typeof(RectTransform)).GetComponent<RectTransform>();rect.SetParent(parent,false);Place(rect,x,y,w,h);return rect;}
    private static void Place(RectTransform rect,float x,float y,float w,float h)
    {rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(0,1);rect.anchoredPosition=new Vector2(x,-y);rect.sizeDelta=new Vector2(w,h);}

    private static GameObject BuildEnvironment(GameObject character,out Camera camera)
    {
        materials.Clear();
        var environment=new GameObject("UnifiedTrainingStation");
        var origin=environment.transform; // Isolated layer and origin keep the old subpage presentations intact.
        var white=Mat("StationWhite",C("EDF4FC"),.35f);var blue=Mat("StationBlue",C("247AD3"),.30f);
        var yellow=Mat("SignalYellow",Yellow,.25f);var steel=Mat("Steel",C("6382A3"),.6f);var floor=Mat("Floor",C("BCD2E6"),.55f);
        Cube(origin,"TrainingDeck",new Vector3(0,-.10f,12),new Vector3(70,.18f,60),floor);
        for(int z=-4;z<30;z+=3)Cube(origin,"DeckJoint",new Vector3(0,.003f,z),new Vector3(65,.008f,.018f),white);
        for(int x=-30;x<31;x+=3)Cube(origin,"DeckJoint",new Vector3(x,.003f,12),new Vector3(.018f,.008f,60),white);
        Cube(origin,"SafetyLineL",new Vector3(-3,.014f,8),new Vector3(.1f,.012f,28),yellow);
        Cube(origin,"SafetyLineR",new Vector3(3,.014f,8),new Vector3(.1f,.012f,28),yellow);
        for(int side=-1;side<=1;side+=2)
        {
            float x=side*7.8f;
            Cube(origin,"WhiteTrainingWing",new Vector3(x,2.2f,11),new Vector3(4.8f,4.4f,13),white);
            Cube(origin,"BlueFacade",new Vector3(x,2.7f,4.4f),new Vector3(4.6f,2.2f,.08f),blue);
            Cube(origin,"FacadeStripe",new Vector3(x,1.50f,4.32f),new Vector3(4.6f,.14f,.1f),yellow);
            Cube(origin,"RoofOverhang",new Vector3(x,4.5f,10.5f),new Vector3(5.4f,.26f,14),white);
            for(int i=0;i<5;i++)Cube(origin,"Window",new Vector3(side*5.37f,2.8f,7+i*2),new Vector3(.06f,1.25f,1.25f),steel);
            Cube(origin,"UpperWalkway",new Vector3(side*4.9f,2.4f,15),new Vector3(1.3f,.2f,20),white);
            for(int i=0;i<10;i++)Cube(origin,"RailPost",new Vector3(side*4.3f,2.95f,6+i*2),new Vector3(.06f,1.0f,.06f),steel);
            Cube(origin,"YellowRailing",new Vector3(side*4.3f,3.45f,15),new Vector3(.075f,.075f,20),yellow);
            for(int i=0;i<3;i++)
            {
                var p=new Vector3(side*(3.5f+i*.15f),.54f,7+i*5);
                Cube(origin,"TrainingCrate",p,new Vector3(1.15f,1.05f,1.15f),white);
                Cube(origin,"CrateAccent",p+new Vector3(0,.43f,-.59f),new Vector3(1.05f,.11f,.035f),yellow);
                Cube(origin,"CrateFace",p+new Vector3(0,0,-.59f),new Vector3(.72f,.5f,.03f),blue);
            }
        }
        Cube(origin,"Bridge",new Vector3(0,4.0f,23),new Vector3(18,.5f,2.2f),white);
        Cube(origin,"BridgeBlueInset",new Vector3(0,3.82f,21.85f),new Vector3(16,.22f,.08f),blue);
        Cube(origin,"BridgeSignal",new Vector3(0,4.32f,21.8f),new Vector3(17,.08f,.08f),yellow);
        for(int i=0;i<7;i++)
        {
            float x=(i-3)*5.5f;float h=5+(i%3)*2.8f;
            Cube(origin,"DistantTower",new Vector3(x,h*.5f,37+(i%2)*6),new Vector3(2.8f,h,3.6f),i%2==0?white:blue);
            Cube(origin,"TowerCrown",new Vector3(x,h+.15f,37+(i%2)*6),new Vector3(3.15f,.3f,4),white);
        }
        // A distant open ring and slender mast echo the reference's airy sci-fi architecture.
        var mast=Primitive(origin,"CommunicationsMast",PrimitiveType.Cylinder,new Vector3(1.7f,8,43),new Vector3(.26f,8,.26f),white);
        for(int i=0;i<32;i++)
        {
            float a=i*Mathf.PI*2/32;var rim=Cube(origin,"SkyRing",new Vector3(1.7f+Mathf.Cos(a)*4,10,43+Mathf.Sin(a)*4),new Vector3(.85f,.16f,.2f),white);
            rim.transform.localRotation=Quaternion.Euler(0,-a*Mathf.Rad2Deg+90,0);
        }
        character.transform.SetParent(origin,false);character.transform.localPosition=Vector3.zero;character.transform.localRotation=Quaternion.Euler(0,180,0);character.transform.localScale=Vector3.one;character.SetActive(true);
        var animator=character.GetComponentInChildren<Animator>();
        if(animator!=null){animator.applyRootMotion=false;animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;animator.Rebind();animator.Update(0);}
        SetLayer(environment.transform,30);
        camera=new GameObject("HomepageCamera",typeof(Camera)).GetComponent<Camera>();camera.transform.SetParent(origin,false);
        camera.transform.localPosition=new Vector3(.22f,1.25f,-4.3f);camera.transform.LookAt(new Vector3(.22f,1.02f,0));
        camera.fieldOfView=32;camera.nearClipPlane=.05f;camera.farClipPlane=150;camera.cullingMask=1<<30;camera.depth=10;camera.clearFlags=CameraClearFlags.Skybox;camera.allowHDR=true;camera.allowMSAA=true;
        var sky=Mat("TrainingSky",new Color(.5f,.7f,1),0,"Skybox/Procedural");
        sky.SetColor("_SkyTint",new Color(.45f,.65f,.95f));sky.SetFloat("_AtmosphereThickness",.6f);sky.SetFloat("_Exposure",1.1f);
        camera.gameObject.AddComponent<Skybox>().material=sky;
        var light=new GameObject("StationSun",typeof(Light)).GetComponent<Light>();light.transform.SetParent(origin,false);light.type=LightType.Directional;light.intensity=1.25f;light.color=new Color(1,.98f,.94f);light.transform.rotation=Quaternion.Euler(43,-30,0);light.cullingMask=1<<30;light.shadows=LightShadows.Soft;light.shadowStrength=.5f;
        var fill=new GameObject("StationFill",typeof(Light)).GetComponent<Light>();fill.transform.SetParent(origin,false);fill.type=LightType.Directional;fill.intensity=.5f;fill.color=new Color(.78f,.88f,1);fill.transform.rotation=Quaternion.Euler(20,145,0);fill.cullingMask=1<<30;
        foreach(var previous in Object.FindObjectsOfType<Light>(true))if(!previous.transform.IsChildOf(origin))previous.cullingMask&=~(1<<30);
        return environment;
    }
    private static Material Mat(string name,Color color,float smooth,string shader="Universal Render Pipeline/Lit")
    {
        if(materials.TryGetValue(name,out var cached))return cached;
        string path=Folder+"/Materials/"+name+".mat";var mat=AssetDatabase.LoadAssetAtPath<Material>(path);
        if(mat==null){mat=new Material(Shader.Find(shader)){name=name};AssetDatabase.CreateAsset(mat,path);}
        if(mat.HasProperty("_BaseColor"))mat.SetColor("_BaseColor",color);if(mat.HasProperty("_Smoothness"))mat.SetFloat("_Smoothness",smooth);
        materials[name]=mat;return mat;
    }
    private static GameObject Cube(Transform parent,string name,Vector3 position,Vector3 scale,Material material)=>Primitive(parent,name,PrimitiveType.Cube,position,scale,material);
    private static GameObject Primitive(Transform parent,string name,PrimitiveType primitive,Vector3 position,Vector3 scale,Material material)
    {
        var go=GameObject.CreatePrimitive(primitive);go.name=name;go.transform.SetParent(parent,false);go.transform.localPosition=position;go.transform.localScale=scale;go.GetComponent<Renderer>().sharedMaterial=material;
        Object.DestroyImmediate(go.GetComponent<Collider>());return go;
    }
    private static void SetLayer(Transform item,int layer){item.gameObject.layer=layer;foreach(Transform child in item)SetLayer(child,layer);}
    private static Texture CaptureMap(Camera camera,GameObject character)
    {
        var position=camera.transform.position;var rotation=camera.transform.rotation;float fov=camera.fieldOfView;
        character.SetActive(false);
        try
        {
            camera.transform.position=new Vector3(-1.2f,3.1f,0);camera.transform.LookAt(new Vector3(0,1.6f,14));camera.fieldOfView=58;
            Render(camera,1000,360,Folder+"/MapPreview.png");
        }
        finally{camera.transform.SetPositionAndRotation(position,rotation);camera.fieldOfView=fov;character.SetActive(true);}
        AssetDatabase.ImportAsset(Folder+"/MapPreview.png",ImportAssetOptions.ForceSynchronousImport);
        return AssetDatabase.LoadAssetAtPath<Texture2D>(Folder+"/MapPreview.png");
    }
    private static void Preview(UnifiedLobbyView view,PlayerAvatarCatalog catalog)
    {
        var profile=new PlayerProfileData{uid=AccountUid.First,accountId="preview",username="preview",avatarId=0,personal=new PersonalProfileData{displayName="初始旅行者"}};
        view.Render(profile,catalog,"",false);view.RenderParty(profile,null,false,false,false,catalog,false);
    }
    [MenuItem("Tools/FPS/Lobby/Capture Unified Homepage")]
    public static void Capture()
    {
        Require(!EditorApplication.isPlayingOrWillChangePlaymode,"Use edit-mode preview capture outside Play Mode.");
        var root=FindRoot();Require(root!=null&&root.homeView!=null,"Apply the unified homepage first.");
        var view=root.homeView;var canvas=view.GetComponent<Canvas>();var camera=root.unifiedEnvironment.GetComponentInChildren<Camera>(true);
        Directory.CreateDirectory(Output);Preview(view,root.avatars);
        var renderMode=canvas.renderMode;var worldCamera=canvas.worldCamera;float distance=canvas.planeDistance;int mask=camera.cullingMask;
        try
        {
            canvas.renderMode=RenderMode.ScreenSpaceCamera;canvas.worldCamera=camera;canvas.planeDistance=1;camera.cullingMask|=1<<5;
            view.GetComponent<UnifiedLobbyLayout>().BindCamera(camera);
            foreach(var size in new[]{new Vector2Int(1920,1080),new Vector2Int(1280,1024),new Vector2Int(2560,1080)})
                Render(camera,size.x,size.y,Output+"/homepage-"+size.x+"x"+size.y+".png",view.GetComponent<UnifiedLobbyLayout>());
        }
        finally{canvas.renderMode=renderMode;canvas.worldCamera=worldCamera;canvas.planeDistance=distance;camera.cullingMask=mask;Canvas.ForceUpdateCanvases();}
    }
    private static void Render(Camera camera,int width,int height,string path,UnifiedLobbyLayout layout=null)
    {
        var oldTarget=camera.targetTexture;var active=RenderTexture.active;var target=RenderTexture.GetTemporary(width,height,24,RenderTextureFormat.ARGB32);Texture2D image=null;
        try
        {
            camera.targetTexture=target;Canvas.ForceUpdateCanvases();
            camera.aspect=(float)width/height;
            if(layout!=null){layout.Refresh();Canvas.ForceUpdateCanvases();}
            foreach(var graphic in camera.gameObject.scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<UnityEngine.UI.Graphic>()))graphic.SetAllDirty();
            Canvas.ForceUpdateCanvases();
            var request=new RenderPipeline.StandardRequest{destination=target};
            if(GraphicsSettings.currentRenderPipeline!=null)RenderPipeline.SubmitRenderRequest(camera,request);else camera.Render();
            RenderTexture.active=target;image=new Texture2D(width,height,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,width,height),0,0);image.Apply();File.WriteAllBytes(path,image.EncodeToPNG());
        }
        finally{camera.targetTexture=oldTarget;camera.ResetAspect();RenderTexture.active=active;if(image!=null)Object.DestroyImmediate(image);RenderTexture.ReleaseTemporary(target);}
    }
    [MenuItem("Tools/FPS/Lobby/Validate Unified Homepage")]
    public static void Validate()
    {
        var root=FindRoot();var checks=new List<string>();
        void Check(bool condition,string name){Require(condition,name);checks.Add(name);}
        Check(root!=null&&root.homeView!=null,"Unified homepage bound to the existing MVC root");
        Check(root.pages.Length==7&&root.pages.Select(p=>p.page).Distinct().Count()==7&&root.pages.Any(p=>p is WardrobeView),"Seven unique authored pages including the wardrobe");
        Check(root.pages.All(p=>p.page!=LobbyPage.MainMenu&&p.page!=LobbyPage.Party),"Old main menu and party page removed from navigation");
        Check(!root.pages.SelectMany(p=>p.buttons).Any(b=>b.action==LobbyAction.Pve),"Single-player action absent from all active page bindings");
        Check(!root.homeView.GetComponentsInChildren<TMP_Text>(true).Any(t=>t.text.Contains("单机")),"Single-player label absent from unified UI");
        Check(root.homeView.memberAvatars.Length==5&&root.homeView.memberNames.Length==5,"Five live account-backed team slots");
        Check(root.homeView.squadCard!=null&&root.homeView.matchCard!=null&&root.homeView.squadCard!=root.homeView.matchCard,"Squad and match controls belong to independent cards");
        Check(root.homeView.teamCount.transform.IsChildOf(root.homeView.squadCard.transform)&&root.homeView.primaryButton.transform.IsChildOf(root.homeView.matchCard.transform),"Each card owns its corresponding controls");
        Check(root.homeView.createPartyButton!=null&&root.homeView.createPartyButton.transform.IsChildOf(root.homeView.matchCard.transform),"A visible match-card action can create a party before the squad card exists");
        Check(root.homeView.buttons.Any(b=>b.action==LobbyAction.Settings)&&root.homeView.buttons.Any(b=>b.action==LobbyAction.CustomPvp)&&root.homeView.buttons.Any(b=>b.action==LobbyAction.Primary),"Settings, custom rooms and primary matchmaking are connected");
        Check(root.gameObject.scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<UnityEngine.EventSystems.EventSystem>(true)).Count()==1,"Exactly one EventSystem");
        Check(root.pages.All(p=>p!=null&&p.buttons.All(b=>b!=null&&b.button!=null)),"All active page/button references are valid");
        Check(root.homeView.GetComponentsInChildren<MonoBehaviour>(true).All(m=>m!=null),"No missing scripts in the authored homepage");
        Check(root.homeCharacter!=null&&root.homeCharacter.transform.IsChildOf(root.unifiedEnvironment.transform),"Authored account character belongs to the new 3D station");
        var model=new LobbyModel();Check(model.Page==LobbyPage.Home,"Default navigation starts directly at Home");
        model.Navigate(LobbyPage.Settings);model.Back();Check(model.Page==LobbyPage.Home,"Subpages return to the same homepage");
        model.Reset(LobbyPage.MainMenu);Check(model.Page==LobbyPage.Home,"Legacy MainMenu requests normalize to Home");
        model.Navigate(LobbyPage.Party);Check(model.Page==LobbyPage.Home,"Party changes stay on the same homepage");
        Directory.CreateDirectory(Output);File.WriteAllText(Output+"/asset-validation.json",JsonUtility.ToJson(new ValidationReport{success=true,checks=checks.ToArray()},true));
    }
    [Serializable]private sealed class ValidationReport{public bool success;public string[] checks;}
    private static LobbyUIRoot FindRoot()=>Object.FindObjectOfType<LobbyUIRoot>(true);
    public static void Save()
    {
        Require(!EditorApplication.isPlayingOrWillChangePlaymode,"Stop Play Mode before saving.");
        var root=FindRoot();Require(root!=null&&root.homeView!=null,"The unified lobby must be open.");
        Validate();
        const string unused=Folder+"/Fonts/UnifiedLobbySans.asset";
        if(AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(unused)!=null&&!AssetDatabase.GetDependencies(PrefabPath,true).Contains(unused))AssetDatabase.DeleteAsset(unused);
        foreach(var guid in AssetDatabase.FindAssets("",new[]{Folder}))
            foreach(var asset in AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GUIDToAssetPath(guid)))
                if(asset!=null&&EditorUtility.IsDirty(asset))AssetDatabase.SaveAssetIfDirty(asset);
        EditorSceneManager.SaveScene(root.gameObject.scene);
    }
    private static Color C(string hex){ColorUtility.TryParseHtmlString("#"+hex,out var c);return c;}
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
