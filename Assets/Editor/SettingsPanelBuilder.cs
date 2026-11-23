using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FPS.Settings;
using FPS.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>Builds the editable settings prefab in the open editor, preserving the authored lobby.</summary>
public static partial class SettingsPanelBuilder
{
    public const string Folder = "Assets/Resource/UI/Setting";
    public const string PrefabPath = Folder + "/SettingsPanel.prefab";
    private const string Output = SettingsEditorTasks.Output;
    private static Font chinese, latin;
    private static readonly Color Cyan = C("3CE8FF"), Ink = C("EAF6FF"), Dim = C("A9C0D8"), Muted = C("5D7591");
    private static readonly Color Line = new Color(120/255f,200/255f,1,.14f);

    [MenuItem("Tools/FPS/Settings/Build And Connect Settings Panel")]
    public static void Build()
    {
        Require(!EditorApplication.isPlayingOrWillChangePlaymode,"Exit Play Mode before building settings.");
        var scene=SceneManager.GetActiveScene();
        Require(scene.path=="Assets/Scenes/GameLobby.unity","Open the authored GameLobby scene first.");
        var root=Object.FindObjectOfType<LobbyUIRoot>(true);
        Require(root!=null,"The open lobby is missing LobbyUIRoot.");
        Require(!File.Exists(PrefabPath),"SettingsPanel already exists. Edit the existing prefab instead of rebuilding it.");
        Directory.CreateDirectory(Output);Directory.CreateDirectory(Folder);
        if(!File.Exists(Output+"/GameLobby.disk.before.unity"))File.Copy(scene.path,Output+"/GameLobby.disk.before.unity");
        if(!File.Exists(Output+"/GameLobby.open.before.unity"))EditorSceneManager.SaveScene(scene,Output+"/GameLobby.open.before.unity",true);
        AssetDatabase.Refresh();PrepareAssets();
        GameObject panel=CreatePanel();
        try
        {
            PrefabUtility.SaveAsPrefabAsset(panel,PrefabPath);
        }
        finally { Object.DestroyImmediate(panel); }
        var instance=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath),scene);
        var ui=scene.GetRootGameObjects().FirstOrDefault(g=>g.name=="UI");
        if(ui!=null)instance.transform.SetParent(ui.transform,false);
        instance.SetActive(false);
        Undo.RegisterCreatedObjectUndo(instance,"Add settings panel");
        Undo.RecordObject(root,"Connect settings page");
        root.pages=root.pages.Concat(new[]{instance.GetComponent<SettingsPanelView>()}).ToArray();
        foreach(var page in root.pages)
        {
            bool changed=false;
            foreach(var b in page.buttons)
                if(b.button!=null&&(b.button.name=="Setting"||b.button.name=="Setting-Btn"))
                { if(!changed)Undo.RecordObject(page,"Connect settings entry");b.action=LobbyAction.Settings;changed=true; }
            if(changed)EditorUtility.SetDirty(page);
        }
        foreach(string path in new[]{"Assets/Resource/UI/Prefab/Mvc/MainMenuPanel.prefab","Assets/Resource/UI/Prefab/Mvc/HomePanel.prefab","Assets/Resource/UI/Prefab/Mvc/PartyPanel.prefab"})
            ConnectPrefabEntry(path);
        EditorUtility.SetDirty(root);EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();Validate();
        Debug.Log("[Settings] Created "+PrefabPath+" and connected the three lobby settings entries.");
    }
    private static void ConnectPrefabEntry(string path)
    {
        var prefab=PrefabUtility.LoadPrefabContents(path);
        try
        {
            var page=prefab.GetComponent<LobbyPageView>();bool changed=false;
            foreach(var b in page.buttons)if(b.button!=null&&(b.button.name=="Setting"||b.button.name=="Setting-Btn")){b.action=LobbyAction.Settings;changed=true;}
            if(changed)PrefabUtility.SaveAsPrefabAsset(prefab,path);
        }
        finally{PrefabUtility.UnloadPrefabContents(prefab);}
    }
    private static void PrepareAssets()
    {
        var importer=AssetImporter.GetAtPath(Folder+"/SettingsBackground.png") as TextureImporter;
        Require(importer!=null,"Missing SettingsBackground.png.");
        importer.textureType=TextureImporterType.Default;importer.mipmapEnabled=false;importer.sRGBTexture=true;importer.maxTextureSize=2048;
        importer.textureCompression=TextureImporterCompression.Uncompressed;importer.wrapMode=TextureWrapMode.Clamp;importer.SaveAndReimport();
        chinese=SettingsLegacyTextMigration.EnsureBodyFont();
        latin=chinese;
    }
    private static GameObject CreatePanel()
    {
        var go=new GameObject("SettingsPanel",typeof(RectTransform),typeof(Canvas),typeof(UnityEngine.UI.CanvasScaler),typeof(UnityEngine.UI.GraphicRaycaster),typeof(CanvasGroup),typeof(SettingsPanelView));
        var canvas=go.GetComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceOverlay;canvas.sortingOrder=100;canvas.overrideSorting=true;
        var scaler=go.GetComponent<UnityEngine.UI.CanvasScaler>();scaler.uiScaleMode=UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;scaler.referenceResolution=new Vector2(1920,1080);scaler.screenMatchMode=UnityEngine.UI.CanvasScaler.ScreenMatchMode.Expand;
        var view=go.GetComponent<SettingsPanelView>();view.page=LobbyPage.Settings;
        var bg=R("Background",go.transform,0,0,1920,1080);Stretch(bg);
        var raw=bg.gameObject.AddComponent<UnityEngine.UI.RawImage>();raw.texture=AssetDatabase.LoadAssetAtPath<Texture2D>(Folder+"/SettingsBackground.png");raw.raycastTarget=true;
        var stage=R("Stage",go.transform,0,0,1920,1080);stage.anchorMin=stage.anchorMax=stage.pivot=new Vector2(.5f,.5f);stage.anchoredPosition=Vector2.zero;
        BuildHeader(stage,view);BuildNavigation(stage,view);
        var main=Panel("ContentPanel",stage,354,126,1518,834,new Color(.025f,.045f,.085f,.65f),26);
        Shape("TopAccent",main,0,0,760,1,Cyan,SettingsShape.ShapeKind.Panel,gradient:true,end:new Color(.235f,.91f,1,0));
        var inner=Outline("InnerFrame",main,6,6,1506,822,new Color(.235f,.91f,1,.055f),20);inner.raycastTarget=false;
        view.watermarkText=T("Watermark",main,"GRAPHICS",384,610,1100,210,164,Color.white,true,TextAnchor.LowerRight);
        view.watermarkNumber=T("WatermarkIndex",main,"01",8,624,230,210,150,Color.white,true);
        var header=R("PageHeader",main,0,0,1518,96);
        Shape("Separator",header,0,95,1518,1,Line);
        view.titleNumber=T("PageNumber",header,"01",30,18,78,74,52,Color.white,true);
        view.titleText=T("PageTitle",header,"画面",120,17,500,40,30,Ink);
        view.titleEnglish=T("PageSubtitle",header,"GRAPHICS · 显示输出与画质",120,59,680,16,10,Muted,true);
        T("ProfileCaption",header,"PROFILE",1290,38,90,20,10,Muted,true);view.profileText=T("ProfileValue",header,"高",1394,37,70,20,12,Cyan);
        view.tabPages=new GameObject[5];view.scrollViews=new UnityEngine.UI.ScrollRect[5];
        var allRows=new List<SettingsRowView>();
        for(int tab=0;tab<5;tab++)
        {
            var scroll=CreateScroll(SettingsSchema.English[tab].Replace(" ",""),main,16,106,1486,718);
            view.tabPages[tab]=scroll.gameObject;view.scrollViews[tab]=scroll;
            var content=scroll.content;var layout=content.gameObject.AddComponent<UnityEngine.UI.VerticalLayoutGroup>();layout.childControlWidth=true;layout.childControlHeight=true;layout.childForceExpandHeight=false;layout.childForceExpandWidth=true;layout.spacing=0;
            var fitter=content.gameObject.AddComponent<UnityEngine.UI.ContentSizeFitter>();fitter.horizontalFit=UnityEngine.UI.ContentSizeFitter.FitMode.Unconstrained;fitter.verticalFit=UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
            for(int section=0;section<SettingsSchema.Sections[tab].Length;section++)
            {
                SectionHeader(content,SettingsSchema.Sections[tab][section],section,1480);
                if(tab==4){BuildCrosshair(content,view,allRows);continue;}
                foreach(var definition in SettingsSchema.Rows.Where(r=>r.tab==tab&&r.section==section))
                {var row=BuildRow(content,definition,1480,372);allRows.Add(row);}
            }
            if(tab==3)
            {
                var help=T("FixedKeysHint",content,"ESC 返回  ·  TAB 计分板  ·  1 / 2 武器槽位  ·  V 射击模式（固定键位）",0,0,1480,42,11,Muted);Height(help.gameObject,42);
            }
            scroll.gameObject.SetActive(tab==0);
        }
        view.rows=allRows.ToArray();BuildFooter(stage,view);BuildOverlays(stage,view);
        return go;
    }
    private static void BuildHeader(RectTransform stage,SettingsPanelView view)
    {
        var header=R("Header",stage,48,40,1824,72);
        Shape("Separator",header,0,71,1824,1,Line);Shape("Accent",header,0,71,180,2,Cyan,gradient:true,end:new Color(.235f,.91f,1,0));
        Shape("LogoOuter",header,0,16,34,34,Cyan,SettingsShape.ShapeKind.Hexagon,outline:true);
        Shape("LogoInner",header,8,24,18,18,new Color(.54f,.42f,1,.9f),SettingsShape.ShapeKind.Hexagon,outline:true);
        Shape("LogoCore",header,13,29,8,8,Cyan,SettingsShape.ShapeKind.Hexagon);
        var brand=T("Brand",header,"PROJECT <color=#3CE8FF>//</color> NOVA",48,12,420,27,22,Ink,true);
        T("BrandSubtitle",header,"新星计划 · 二次元战术射击",48,42,420,15,10,Muted);
        T("Location",header,"<color=#3CE8FF>//</color>   设置   <color=#3CE8FF>/</color>   CONFIGURATION",850,23,420,24,13,Dim);
        Shape("StatusLight",header,1360,31,5,5,Cyan,SettingsShape.ShapeKind.Diamond);
        T("LocalConfig",header,"LOCAL CONFIG",1375,22,120,22,11,Muted,true);
        Shape("ProfileSeparator",header,1500,17,1,36,Line);
        Shape("Avatar",header,1522,18,34,34,Cyan,SettingsShape.ShapeKind.Hexagon);
        T("AvatarGlyph",header,"E",1522,18,34,34,17,C("04121A"),true,TextAnchor.MiddleCenter);
        view.headerPlayerName=T("PlayerName",header,"PLAYER",1568,15,134,22,13,Ink);view.headerPlayerName.supportRichText=false;
        T("ProfileSubtitle",header,"本地设置",1568,39,130,15,10,Muted);
        view.backButton=Button("Back",header,"返回",1714,17,110,36,false,out _);
        T("EscapeKey",view.backButton.transform,"ESC",7,6,37,23,11,Cyan,true,TextAnchor.MiddleCenter);
        var label=view.backButton.transform.Find("Label").GetComponent<UnityEngine.UI.Text>();label.rectTransform.anchoredPosition=new Vector2(44,0);label.rectTransform.sizeDelta=new Vector2(64,36);label.fontSize=12;
    }
    private static void BuildNavigation(RectTransform stage,SettingsPanelView view)
    {
        var nav=Panel("Navigation",stage,48,126,292,834,new Color(.025f,.045f,.085f,.58f),22);
        T("NavigationTitle",nav,"设置",24,22,70,24,16,Ink);
        T("NavigationEnglish",nav,"OPTIONS",94,27,90,16,10,Muted,true);
        T("SystemCode",nav,"SYS_04",224,28,50,14,9,Cyan,true);
        Shape("Separator",nav,0,63,292,1,Line);
        view.tabButtons=new UnityEngine.UI.Button[5];view.tabHighlights=new SettingsShape[5];view.tabLabels=new UnityEngine.UI.Text[5];view.tabEnglish=new UnityEngine.UI.Text[5];
        for(int i=0;i<5;i++)
        {
            float y=74+i*78;
            var button=Button("Tab"+(i+1),nav,"",0,y,292,78,false,out var background,false);view.tabButtons[i]=button;view.tabHighlights[i]=background;
            background.color=i==0?new Color(.235f,.91f,1,.18f):Color.clear;
            Shape("ActiveEdge",button.transform,0,0,3,78,Cyan,gradient:true,end:C("8A6BFF"));
            // The edge inherits the tab background's selected opacity through this graphic reference.
            var edge=button.transform.Find("ActiveEdge").GetComponent<SettingsShape>();edge.color=i==0?Cyan:Color.clear;
            Shape("Icon",button.transform,29,27,22,22,i==0?Cyan:Muted,(SettingsShape.ShapeKind)((int)SettingsShape.ShapeKind.Monitor+i));
            T("Index",button.transform,(i+1).ToString("00"),221,2,60,62,51,new Color(.235f,.91f,1,.08f),true,TextAnchor.UpperRight);
            view.tabLabels[i]=T("Chinese",button.transform,SettingsSchema.Titles[i],74,14,150,29,19,i==0?Ink:Dim);
            view.tabEnglish[i]=T("English",button.transform,(i+1).ToString("00")+" / "+SettingsSchema.English[i],74,47,200,16,9.5f,i==0?Cyan:Muted,true);
            Shape("Separator",button.transform,0,77,292,1,new Color(.47f,.78f,1,.07f));
        }
        Shape("QuickSeparator",nav,0,623,292,1,Line);
        T("QuickCaption",nav,"快速预设",24,642,140,22,11,Dim);
        T("QuickEnglish",nav,"QUICK PROFILE",158,647,110,16,9,Muted,true);
        string[] qualities=SettingsSchema.QualityNames;view.quickButtons=new UnityEngine.UI.Button[qualities.Length];view.quickHighlights=new SettingsShape[qualities.Length];
        float width=(244-6*(qualities.Length-1))/qualities.Length;
        for(int i=0;i<qualities.Length;i++){view.quickButtons[i]=Button("QuickQuality"+i,nav,qualities[i],24+i*(width+6),680,width,36,false,out var highlight);view.quickHighlights[i]=highlight;}
        Shape("FootSeparator",nav,0,754,292,1,Line);Shape("FootHex",nav,24,785,22,22,Cyan,SettingsShape.ShapeKind.Hexagon,outline:true);
        T("Build",nav,"BUILD 2026.11.16",58,776,210,18,10,Dim,true);
        T("Input",nav,"INPUT · KEYBOARD / MOUSE",58,798,210,16,9,Muted,true);
    }
    private static void BuildFooter(RectTransform stage,SettingsPanelView view)
    {
        var footer=R("Footer",stage,48,976,1824,76);Shape("Separator",footer,0,0,1824,1,Line);Shape("Accent",footer,0,0,120,1,Cyan);
        Outline("StatusOutline",footer,0,24,160,30,Line,0);
        Shape("StatusDot",footer,13,37,4,4,C("8A6BFF"),SettingsShape.ShapeKind.Diamond);
        view.saveStatus=T("SaveStatus",footer,"配置已同步",27,27,125,24,11,C("8A6BFF"));
        view.saveTime=T("LastSaved",footer,"LAST SAVE · --:--:--",176,29,280,21,11,Muted,true);
        T("KeyboardHint",footer,"↑↓  分类     ←→  调整      CTRL + ENTER  应用",878,29,480,24,10,Muted);
        view.resetButton=Button("Reset",footer,"恢复默认",1400,16,150,44,false,out _);
        view.cancelButton=Button("Cancel",footer,"取消",1560,16,116,44,false,out _);
        view.applyButton=Button("Apply",footer,"应用",1686,16,138,44,true,out _);
    }
    private static void BuildOverlays(RectTransform stage,SettingsPanelView view)
    {
        var blocker=R("KeyCaptureBlocker",stage,0,0,1920,1080);var image=blocker.gameObject.AddComponent<UnityEngine.UI.Image>();image.color=Color.clear;image.raycastTarget=true;blocker.gameObject.SetActive(false);view.captureBlocker=blocker.gameObject;
        var feedback=Panel("Feedback",stage,580,888,760,46,new Color(.025f,.055f,.10f,.98f),10);view.feedback=feedback.gameObject;
        Shape("Accent",feedback,0,9,3,28,Cyan);view.feedbackText=T("Message",feedback,"",24,7,712,32,13,Ink,false,TextAnchor.MiddleCenter);feedback.gameObject.SetActive(false);
        var modal=R("Modal",stage,0,0,1920,1080);var shade=modal.gameObject.AddComponent<UnityEngine.UI.Image>();shade.color=new Color(.005f,.01f,.025f,.85f);shade.raycastTarget=true;view.modal=modal.gameObject;
        var box=Panel("Dialog",modal,660,365,600,320,new Color(.025f,.055f,.10f,1),22);
        T("Kicker",box,"SETTINGS // CONFIRM",30,24,520,22,11,Cyan,true);
        view.modalTitle=T("Title",box,"恢复默认设置？",30,69,540,42,26,Ink);
        view.modalBody=T("Body",box,"",30,133,540,78,14,Dim);view.modalBody.horizontalOverflow=HorizontalWrapMode.Wrap;
        view.modalCancel=Button("Cancel",box,"取消",254,246,132,44,false,out _);
        view.modalConfirm=Button("Confirm",box,"确认",398,246,172,44,true,out var unused);view.modalConfirmLabel=view.modalConfirm.transform.Find("Label").GetComponent<UnityEngine.UI.Text>();
        modal.gameObject.SetActive(false);
    }
    private static void SectionHeader(RectTransform parent,string pair,int index,float width)
    {
        var parts=pair.Split('|');var header=R("Section"+(index+1),parent,0,0,width,44);Height(header.gameObject,44);
        var background=Shape("Background",header,0,0,width,44,new Color(.03f,.055f,.10f,.94f));Stretch(background.rectTransform);
        Shape("TopAccent",header,0,0,62,1,Cyan,gradient:true,end:Color.clear);
        Shape("Glyph",header,4,18,9,9,Cyan,SettingsShape.ShapeKind.Diamond);
        T("Chinese",header,parts[0],24,10,130,24,13.5f,Ink);
        float x=24+parts[0].Length*16+13;
        T("English",header,parts[1],x,14,180,18,9,Muted,true);
        Shape("Rule",header,x+180,22,width-x-220,1,new Color(.47f,.78f,1,.26f),SettingsShape.ShapeKind.DashedLine);
        T("Number",header,(index+1).ToString("00"),width-34,14,30,18,10,Muted,true,TextAnchor.MiddleRight);
        Shape("Separator",header,0,43,width,1,Line);
    }
    private static SettingsRowView BuildRow(RectTransform parent,SettingDefinition d,float width,float controlWidth)
    {
        var row=R(d.id,parent,0,0,width,66);Height(row.gameObject,66);var view=row.gameObject.AddComponent<SettingsRowView>();view.settingId=d.id;
        var bg=Shape("Background",row,0,0,width,66,new Color(.018f,.035f,.065f,.40f));Stretch(bg.rectTransform);
        Shape("Separator",row,0,65,width,1,new Color(.47f,.78f,1,.05f));
        float controlX=width-controlWidth-4;
        T("Label",row,d.label,4,10,controlX-20,22,14,Ink);
        T("English",row,d.english,4,33,controlX-20,14,9,Muted,true);
        if(d.hint.Length>0)T("Hint",row,d.hint,4,49,controlX-20,14,10.5f,Muted);
        float labelWidth=d.label.Length*16+26;
        if(controlX-labelWidth>30)
        {Shape("Leader",row,labelWidth,21,controlX-labelWidth-26,1,new Color(.51f,.80f,.98f,.28f),SettingsShape.ShapeKind.DashedLine);Shape("Endpoint",row,controlX-20,19,5,5,new Color(.235f,.91f,1,.45f),SettingsShape.ShapeKind.Diamond,outline:true);}
        var control=R("Control",row,controlX,0,controlWidth,66);
        if(d.kind==SettingKind.Segment)
        {
            var outline=Outline("Border",control,0,16,controlWidth,36,new Color(.47f,.78f,1,.22f),0);outline.raycastTarget=false;
            string[] options=d.id=="quality"?SettingsSchema.QualityNames:d.options;
            view.choices=new UnityEngine.UI.Button[options.Length];view.choiceBackgrounds=new SettingsShape[options.Length];view.choiceLabels=new UnityEngine.UI.Text[options.Length];
            float w=(controlWidth-6-2*(options.Length-1))/options.Length;
            int selected=d.id=="fpsCap"?2:d.id=="displayMode"?1:d.id=="quality"?2:d.id=="x_style"?3:0;
            for(int i=0;i<options.Length;i++)
            {
                var button=Button("Option"+i,control,options[i],3+i*(w+2),19,w,30,i==selected,out var fill,false);fill.shape=SettingsShape.ShapeKind.Slanted;fill.bevel=8;
                view.choices[i]=button;view.choiceBackgrounds[i]=fill;view.choiceLabels[i]=button.transform.Find("Label").GetComponent<UnityEngine.UI.Text>();view.choiceLabels[i].fontSize=12;
            }
        }
        else if(d.kind==SettingKind.Slider)BuildSlider(control,view,d,controlWidth);
        else if(d.kind==SettingKind.Toggle)BuildToggle(control,view,(bool)d.Get(new GameSettingsData()),controlWidth);
        else if(d.kind==SettingKind.Select)BuildDropdown(control,view,d,controlWidth);
        else if(d.kind==SettingKind.Key)
        {
            view.keyButton=Button("Keycap",control,SettingsPlayerInput.DisplayName((string)d.Get(new GameSettingsData())),(controlWidth-150)/2,17,150,32,false,out var fill);
            fill.shape=SettingsShape.ShapeKind.Slanted;fill.bevel=9;view.valueText=view.keyButton.transform.Find("Label").GetComponent<UnityEngine.UI.Text>();view.valueText.font=latin;view.valueText.fontSize=14;
            Shape("Underline",view.keyButton.transform,9,31,132,1,new Color(.235f,.91f,1,.5f));
        }
        else
        {Outline("ReadOnlyBorder",control,0,17,controlWidth,32,new Color(.47f,.78f,1,.18f),8);view.valueText=T("ReadOnlyValue",control,"系统默认",12,20,controlWidth-24,26,12.5f,Dim);}
        return view;
    }
    private static void BuildSlider(RectTransform parent,SettingsRowView row,SettingDefinition d,float width)
    {
        var root=R("Slider",parent,0,16,width-92,32);var slider=root.gameObject.AddComponent<UnityEngine.UI.Slider>();row.slider=slider;
        var hit=root.gameObject.AddComponent<UnityEngine.UI.Image>();hit.color=Color.clear;hit.raycastTarget=true;
        Shape("Track",root,0,15,width-92,2,new Color(.51f,.80f,.98f,.24f));
        var fillArea=R("FillArea",root,0,15,width-92,2);var fill=Shape("Fill",fillArea,0,0,width-92,2,Cyan);Stretch(fill.rectTransform);slider.fillRect=fill.rectTransform;
        fill.rectTransform.sizeDelta=Vector2.zero;
        var handles=R("HandleArea",root,0,16,width-92,0);var knob=Shape("Handle",handles,0,0,11,11,Cyan,SettingsShape.ShapeKind.Diamond);knob.rectTransform.pivot=new Vector2(.5f,.5f);knob.raycastTarget=true;
        slider.handleRect=knob.rectTransform;slider.targetGraphic=knob;slider.direction=UnityEngine.UI.Slider.Direction.LeftToRight;
        slider.minValue=d.min;slider.maxValue=d.max;slider.wholeNumbers=d.step>=1;slider.value=Convert.ToSingle(d.Get(new GameSettingsData()));
        for(int i=0;i<5;i++)Shape("Tick"+i,root,i*(width-92)/4,25,1,i==0||i==4?9:5,new Color(.47f,.78f,1,.3f));
        row.valueText=T("Value",parent,d.Format(new GameSettingsData()),width-80,15,80,34,17,Cyan,true,TextAnchor.MiddleRight);
    }
    private static void BuildToggle(RectTransform parent,SettingsRowView row,bool on,float width)
    {
        var root=R("Toggle",parent,width-107,18,107,28);var toggle=root.gameObject.AddComponent<UnityEngine.UI.Toggle>();row.toggle=toggle;
        row.toggleTrack=Shape("Track",root,0,0,68,28,on?new Color(.12f,.61f,.73f,.75f):new Color(.04f,.08f,.14f,.9f),SettingsShape.ShapeKind.Slanted);row.toggleTrack.bevel=11;row.toggleTrack.raycastTarget=true;toggle.targetGraphic=row.toggleTrack;
        Outline("Outline",root,0,0,68,28,new Color(.47f,.80f,.96f,.4f),0,SettingsShape.ShapeKind.Slanted,11);
        var knob=Shape("Knob",root,on?38:11,6,17,17,on?C("041018"):Dim,SettingsShape.ShapeKind.Diamond);row.toggleKnob=knob.rectTransform;
        row.toggleKnob.anchorMin=row.toggleKnob.anchorMax=new Vector2(0,1);row.toggleKnob.pivot=new Vector2(.5f,.5f);row.toggleKnob.anchoredPosition=new Vector2(on?47:20,-14);
        toggle.graphic=null;toggle.isOn=on;
        row.valueText=T("Value",root,on?"ON":"OFF",81,3,26,24,10,on?Cyan:Muted,true);
    }
    private static void BuildDropdown(RectTransform parent,SettingsRowView row,SettingDefinition d,float width)
    {
        var root=Panel("Dropdown",parent,0,17,width,32,new Color(.015f,.035f,.07f,.86f),8);
        var dropdown=root.gameObject.AddComponent<SettingsDropdown>();row.dropdown=dropdown;dropdown.targetGraphic=root.GetComponent<SettingsShape>();dropdown.targetGraphic.raycastTarget=true;
        var caption=T("Label",root,d.id=="resolution"?"1920 × 1080":"青色",12,2,width-44,28,12.5f,Ink);dropdown.captionText=caption;
        T("Caret",root,"v",width-31,2,24,26,13,Cyan,true,TextAnchor.MiddleCenter);
        var template=Panel("Template",root,0,36,width,190,new Color(.025f,.055f,.10f,1),8);dropdown.template=template;
        template.anchorMin=new Vector2(0,0);template.anchorMax=new Vector2(1,0);template.pivot=new Vector2(.5f,1);template.anchoredPosition=new Vector2(0,-4);template.sizeDelta=new Vector2(0,190);
        var scroll=template.gameObject.AddComponent<UnityEngine.UI.ScrollRect>();scroll.horizontal=false;scroll.vertical=true;scroll.movementType=UnityEngine.UI.ScrollRect.MovementType.Clamped;scroll.scrollSensitivity=30;
        var viewport=R("Viewport",template,4,4,width-8,182);Stretch(viewport,4);var mask=viewport.gameObject.AddComponent<UnityEngine.UI.Image>();mask.color=Color.white;mask.raycastTarget=true;viewport.gameObject.AddComponent<UnityEngine.UI.Mask>().showMaskGraphic=false;
        var content=R("Content",viewport,0,0,width-8,32);content.anchorMin=new Vector2(0,1);content.anchorMax=Vector2.one;content.sizeDelta=new Vector2(0,32);
        var item=R("Item",content,0,0,width-8,32);item.anchorMin=new Vector2(0,.5f);item.anchorMax=new Vector2(1,.5f);item.pivot=new Vector2(.5f,.5f);item.sizeDelta=new Vector2(0,32);item.anchoredPosition=Vector2.zero;
        var toggle=item.gameObject.AddComponent<UnityEngine.UI.Toggle>();var itemBg=Shape("Background",item,0,0,width-8,32,new Color(.235f,.91f,1,.10f));Stretch(itemBg.rectTransform);itemBg.raycastTarget=true;toggle.targetGraphic=itemBg;
        var check=Shape("Selected",item,0,0,3,32,Cyan);toggle.graphic=check;
        var label=T("ItemLabel",item,"OPTION",12,0,width-32,32,12.5f,Ink);dropdown.itemText=label;
        scroll.viewport=viewport;scroll.content=content;template.gameObject.SetActive(false);
        dropdown.AddOptions(new List<string>(d.id=="resolution"?SettingsSchema.Resolutions:d.options));
        dropdown.value=d.id=="resolution"?Mathf.Max(0,dropdown.options.FindIndex(o=>o.text==new GameSettingsData().resolution)):0;
    }
    private static void BuildCrosshair(RectTransform content,SettingsPanelView view,List<SettingsRowView> rows)
    {
        var body=R("CrosshairEditor",content,0,0,1480,582);Height(body.gameObject,582);
        var field=Panel("PreviewField",body,4,16,830,528,new Color(.012f,.03f,.055f,.94f),0);
        var grid=Shape("Grid",field,0,0,830,528,new Color(.47f,.78f,1,.025f),SettingsShape.ShapeKind.Grid);Stretch(grid.rectTransform);
        foreach(float diameter in new[]{112f,224f})
        {var ring=Shape("Ring"+diameter,field,(830-diameter)/2,(528-diameter)/2,diameter,diameter,new Color(.47f,.78f,1,.15f),SettingsShape.ShapeKind.Ring);ring.stroke=1;}
        T("Range",field,"RANGE · 100 M",14,12,400,20,10,Dim,true);
        for(int i=0;i<4;i++){float x=i%2==0?8:808,y=i<2?8:506;Shape("CornerH"+i,field,x,y,14,1,new Color(.235f,.91f,1,.45f));Shape("CornerV"+i,field,x,y,1,14,new Color(.235f,.91f,1,.45f));}
        view.preview=Crosshair(field,"Crosshair",415,264);
        var zoom=Panel("Zoom",field,16,362,150,150,new Color(.01f,.025f,.05f,.94f),10);view.zoomPreview=Crosshair(zoom,"Crosshair",75,75);view.zoomPreview.rectTransform.localScale=Vector3.one*2.3f;
        T("ZoomLabel",zoom,"ZOOM ×2.3",68,127,76,18,10,Dim,true,TextAnchor.MiddleRight);
        view.previewDescription=T("PreviewDescription",field,"十字+点 · 青色 · 长7 隙4 粗2",284,498,526,22,11,Cyan,false,TextAnchor.MiddleRight);
        T("ScaleLabel",body,"点击参数即时预览 · 与游戏内保持 1:1",4,554,600,20,10.5f,Muted);
        view.previewMode=T("PreviewMode",body,"DYNAMIC SPREAD",1230,554,246,20,10,Muted,true,TextAnchor.MiddleRight);
        var parameters=R("Parameters",body,860,16,620,528);
        int index=0;foreach(var d in SettingsSchema.Rows.Where(r=>r.tab==4))
        {var row=BuildRow(parameters,d,620,300);row.GetComponent<RectTransform>().anchoredPosition=new Vector2(0,-index*66);rows.Add(row);index++;}
    }
    private static SettingsCrosshairGraphic Crosshair(Transform parent,string name,float x,float y)
    {var rect=R(name,parent,x,y,100,100);rect.pivot=new Vector2(.5f,.5f);var graphic=rect.gameObject.AddComponent<SettingsCrosshairGraphic>();graphic.color=Cyan;graphic.raycastTarget=false;return graphic;}
    private static UnityEngine.UI.ScrollRect CreateScroll(string name,Transform parent,float x,float y,float width,float height)
    {
        var root=R(name,parent,x,y,width,height);var scroll=root.gameObject.AddComponent<UnityEngine.UI.ScrollRect>();scroll.horizontal=false;scroll.vertical=true;scroll.scrollSensitivity=40;scroll.movementType=UnityEngine.UI.ScrollRect.MovementType.Clamped;
        var viewport=R("Viewport",root,0,0,width-8,height);var image=viewport.gameObject.AddComponent<UnityEngine.UI.Image>();image.color=Color.white;image.raycastTarget=true;viewport.gameObject.AddComponent<UnityEngine.UI.Mask>().showMaskGraphic=false;
        var content=R("Content",viewport,0,0,width-8,0);content.anchorMin=new Vector2(0,1);content.anchorMax=Vector2.one;content.pivot=new Vector2(.5f,1);content.sizeDelta=Vector2.zero;content.anchoredPosition=Vector2.zero;scroll.viewport=viewport;scroll.content=content;
        var bar=R("Scrollbar",root,width-4,0,3,height);var bg=bar.gameObject.AddComponent<UnityEngine.UI.Image>();bg.color=new Color(.47f,.78f,1,.04f);var scrollbar=bar.gameObject.AddComponent<UnityEngine.UI.Scrollbar>();scrollbar.direction=UnityEngine.UI.Scrollbar.Direction.BottomToTop;
        var area=R("SlidingArea",bar,0,0,3,height);Stretch(area);var handle=Shape("Handle",area,0,0,3,height,Cyan);Stretch(handle.rectTransform);handle.raycastTarget=true;scrollbar.targetGraphic=handle;scrollbar.handleRect=handle.rectTransform;
        scroll.verticalScrollbar=scrollbar;scroll.verticalScrollbarVisibility=UnityEngine.UI.ScrollRect.ScrollbarVisibility.AutoHide;
        return scroll;
    }
    private static RectTransform R(string name,Transform parent,float x,float y,float w,float h)
    {var go=new GameObject(name,typeof(RectTransform));var r=go.GetComponent<RectTransform>();r.SetParent(parent,false);r.anchorMin=r.anchorMax=r.pivot=new Vector2(0,1);r.sizeDelta=new Vector2(w,h);r.anchoredPosition=new Vector2(x,-y);return r;}
    private static void Stretch(RectTransform r,float inset=0)
    {r.anchorMin=Vector2.zero;r.anchorMax=Vector2.one;r.pivot=new Vector2(.5f,.5f);r.offsetMin=new Vector2(inset,inset);r.offsetMax=new Vector2(-inset,-inset);}
    private static UnityEngine.UI.Text T(string name,Transform parent,string text,float x,float y,float w,float h,float size,Color color,bool english=false,TextAnchor alignment=TextAnchor.MiddleLeft)
    {var r=R(name,parent,x,y,w,h);var t=r.gameObject.AddComponent<UnityEngine.UI.Text>();t.font=english?latin:chinese;t.text=text;t.fontSize=Mathf.RoundToInt(size);t.color=color;t.alignment=alignment;t.raycastTarget=false;t.horizontalOverflow=HorizontalWrapMode.Overflow;t.verticalOverflow=VerticalWrapMode.Overflow;t.supportRichText=true;return t;}
    private static SettingsShape Shape(string name,Transform parent,float x,float y,float w,float h,Color color,SettingsShape.ShapeKind kind=SettingsShape.ShapeKind.Panel,bool outline=false,bool gradient=false,Color end=default)
    {var r=R(name,parent,x,y,w,h);var shape=r.gameObject.AddComponent<SettingsShape>();shape.shape=kind;shape.color=color;shape.outline=outline;shape.gradient=gradient;shape.endColor=end;shape.raycastTarget=false;return shape;}
    private static SettingsShape Outline(string name,Transform parent,float x,float y,float w,float h,Color color,float cut,SettingsShape.ShapeKind kind=SettingsShape.ShapeKind.Panel,float bevel=0)
    {var shape=Shape(name,parent,x,y,w,h,color,kind,true);shape.bevel=bevel>0?bevel:cut;return shape;}
    private static RectTransform Panel(string name,Transform parent,float x,float y,float w,float h,Color color,float bevel)
    {var shape=Shape(name,parent,x,y,w,h,color);shape.bevel=bevel;Outline("Border",shape.transform,0,0,w,h,Line,bevel);return shape.rectTransform;}
    private static UnityEngine.UI.Button Button(string name,Transform parent,string label,float x,float y,float w,float h,bool primary,out SettingsShape fill,bool border=true)
    {
        fill=Shape(name,parent,x,y,w,h,primary?Cyan:new Color(.025f,.045f,.08f,.7f));fill.bevel=10;fill.raycastTarget=true;
        var button=fill.gameObject.AddComponent<UnityEngine.UI.Button>();button.targetGraphic=fill;
        var colors=button.colors;colors.normalColor=Color.white;colors.highlightedColor=new Color(.8f,1,1,1);colors.pressedColor=new Color(.6f,.8f,.9f,1);colors.selectedColor=Color.white;colors.disabledColor=new Color(1,1,1,.35f);colors.fadeDuration=.18f;button.colors=colors;
        if(border)Outline("Border",fill.transform,0,0,w,h,new Color(.47f,.78f,1,.3f),10);
        var text=T("Label",fill.transform,label,0,0,w,h,13,primary?C("04121A"):Dim,false,TextAnchor.MiddleCenter);
        return button;
    }
    private static void Height(GameObject go,float height)
    {var element=go.AddComponent<UnityEngine.UI.LayoutElement>();element.minHeight=height;element.preferredHeight=height;element.flexibleHeight=0;}
    private static Color C(string hex){ColorUtility.TryParseHtmlString("#"+hex,out var result);return result;}
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
