using TMPro;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FPS.Settings;
using FPS.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

public static partial class SettingsPanelBuilder
{
    [Serializable] private sealed class Report { public bool success; public List<string> checks=new List<string>(); public List<string> errors=new List<string>(); }
    [MenuItem("Tools/FPS/Settings/Validate Settings Prefab")]
    public static void Validate()
    {
        var report=new Report();
        var go=PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            var view=go.GetComponent<SettingsPanelView>();
            Check(report,view!=null&&view.rows.Length==34,"All 34 settings rows are authored in the prefab.");
            Check(report,view.tabPages.Length==5,"Five complete settings pages.");
            Check(report,go.GetComponentsInChildren<TMP_Text>(true).Length==0 && go.GetComponentsInChildren<TMP_Dropdown>(true).Length==0,"No TMP text or TMP dropdown components remain in settings.");
            Check(report,view.rows.Where(r=>SettingsSchema.Find(r.settingId).kind==SettingKind.Select).All(r=>r.dropdown!=null&&r.dropdown.captionText!=null&&r.dropdown.itemText!=null&&r.dropdown.template!=null),"Both legacy dropdowns retain their template and text references.");
            Check(report,view.tabLabels.All(t=>t!=null)&&view.rows.SelectMany(r=>r.choiceLabels).All(t=>t!=null),"Legacy tab and choice labels are connected.");
            Check(report,view.rows.Count(r=>r.settingId=="quality")==1&&!view.rows.Any(r=>new[]{"aa","shadow","aniso","outline","preset"}.Contains(r.settingId)),"The quality section contains only Graphics Quality.");
            Check(report,view.rows.Count(r=>r.settingId.StartsWith("kb_"))==11&&!view.rows.Any(r=>new[]{"kb_melee","kb_skill","kb_ult"}.Contains(r.settingId)),"Eleven key bindings; melee and abilities are omitted.");
            Check(report,go.GetComponent<UnityEngine.UI.GraphicRaycaster>()!=null,"The settings canvas has a raycaster.");
            Check(report,go.GetComponentsInChildren<UnityEngine.UI.Graphic>(true).All(g=>g.GetComponent<CanvasRenderer>()!=null),"Every visible graphic has a CanvasRenderer.");
            Check(report,view.rows.Where(r=>SettingsSchema.Find(r.settingId).kind==SettingKind.Info).All(r=>r.valueText.text=="系统默认"),"Audio output follows the system default.");
            foreach(var scroll in view.scrollViews)
                Check(report,scroll.content!=null&&scroll.viewport!=null&&scroll.viewport.GetComponent<UnityEngine.UI.Mask>()!=null&&scroll.viewport.GetComponent<UnityEngine.UI.Image>()!=null,"Valid ScrollRect hierarchy: "+scroll.name);
            foreach(var row in view.rows)
                Check(report,row.GetComponentsInChildren<MonoBehaviour>(true).All(c=>c!=null),"No missing components: "+row.settingId);
            var root=Object.FindObjectOfType<LobbyUIRoot>(true);
            if(root!=null)
            {
                Check(report,root.pages.Any(p=>p!=null&&p.page==LobbyPage.Settings),"GameLobby references the settings prefab.");
                int entries=root.pages.SelectMany(p=>p.buttons).Count(b=>b.button!=null&&b.action==LobbyAction.Settings);
                Check(report,root.homeView!=null ? root.homeView.buttons.Any(b=>b.action==LobbyAction.Settings&&b.button!=null) : entries==3,"The active homepage settings entry is connected.");
                Check(report,root.gameObject.scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<UnityEngine.EventSystems.EventSystem>(true)).Count()==1,"Exactly one EventSystem in GameLobby.");
            }
            foreach(var text in go.GetComponentsInChildren<UnityEngine.UI.Text>(true))
                if(text.font==null)report.errors.Add("Missing font: "+text.name);
        }
        finally{PrefabUtility.UnloadPrefabContents(go);}
        report.success=report.errors.Count==0;Directory.CreateDirectory(Output);File.WriteAllText(Output+"/prefab-validation.json",JsonUtility.ToJson(report,true));
        Require(report.success,string.Join("\n",report.errors));
    }
    private static void Check(Report report,bool passed,string description)
    {if(passed)report.checks.Add(description);else report.errors.Add(description);}

    public static void RepairRenderers()
    {
        var go=PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            foreach(var graphic in go.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
                if(graphic.GetComponent<CanvasRenderer>()==null)graphic.gameObject.AddComponent<CanvasRenderer>();
            foreach(var text in go.GetComponentsInChildren<UnityEngine.UI.Text>(true))
                if(text.name=="Caret")text.text="v";
            PrefabUtility.SaveAsPrefabAsset(go,PrefabPath);
        }
        finally{PrefabUtility.UnloadPrefabContents(go);}
        AssetDatabase.SaveAssets();
    }

    public static void RefineLayout()
    {
        var go=PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            var view=go.GetComponent<SettingsPanelView>();
            view.titleNumber.fontSize=52;view.titleNumber.rectTransform.sizeDelta=new Vector2(78,74);
            view.watermarkText.rectTransform.anchoredPosition=new Vector2(384,-610);view.watermarkText.rectTransform.sizeDelta=new Vector2(1100,210);
            view.watermarkNumber.rectTransform.anchoredPosition=new Vector2(8,-624);view.watermarkNumber.rectTransform.sizeDelta=new Vector2(230,210);
            foreach(var scroll in view.scrollViews)Stretch(scroll.verticalScrollbar.handleRect);
            foreach(var row in view.rows.Where(r=>r.slider!=null))
            {
                var slider=row.slider;
                slider.fillRect.sizeDelta=Vector2.zero;
                var area=(RectTransform)slider.handleRect.parent;area.anchoredPosition=new Vector2(0,-16);area.sizeDelta=new Vector2(area.sizeDelta.x,0);
                slider.handleRect.pivot=new Vector2(.5f,.5f);slider.handleRect.anchoredPosition=Vector2.zero;slider.handleRect.sizeDelta=new Vector2(11,11);
            }
            foreach(var button in view.tabButtons){var border=button.transform.Find("Border");if(border!=null)border.gameObject.SetActive(false);}
            var backLabel=view.backButton.transform.Find("Label").GetComponent<RectTransform>();backLabel.anchoredPosition=new Vector2(44,0);backLabel.sizeDelta=new Vector2(64,36);
            var arc=go.transform.Find("Stage/ContentPanel/DecorativeArc");if(arc!=null)arc.gameObject.SetActive(false);
            var grid=go.transform.Find("Stage/ContentPanel/CROSSHAIR/Viewport/Content/CrosshairEditor/PreviewField/Grid").GetComponent<SettingsShape>();grid.color=new Color(.47f,.78f,1,.025f);
            var switchRow=view.rows.First(r=>r.settingId=="kb_switch");switchRow.valueText.text="Q";
            var content=view.scrollViews[3].content;
            content.Find("FixedKeysHint").GetComponent<UnityEngine.UI.Text>().text="ESC 返回  ·  TAB 计分板  ·  1 / 2 武器槽位  ·  V 射击模式（固定键位）";
            if(switchRow.transform.Find("Hint")==null)
            {
                chinese=SettingsLegacyTextMigration.EnsureBodyFont();
                T("Hint",switchRow.transform,SettingsSchema.Find("kb_switch").hint,4,49,1090,14,10.5f,Muted);
            }
            PrefabUtility.SaveAsPrefabAsset(go,PrefabPath);
        }
        finally{PrefabUtility.UnloadPrefabContents(go);}
        AssetDatabase.SaveAssets();
    }

    public static void RefineTypography()
    {
        chinese=SettingsLegacyTextMigration.EnsureBodyFont();
        var go=PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            foreach(var text in go.GetComponentsInChildren<UnityEngine.UI.Text>(true))
                if(text.font==null)text.font=chinese;
            PrefabUtility.SaveAsPrefabAsset(go,PrefabPath);
        }
        finally{PrefabUtility.UnloadPrefabContents(go);}
        AssetDatabase.SaveAssets();
    }
    public static void ConfigurePrivateFallback(){RefineTypography();}

    [MenuItem("Tools/FPS/Settings/Capture Settings Pages")]
    public static void Capture()
    {
        Require(!EditorApplication.isPlayingOrWillChangePlaymode,"Capture uses an isolated editor preview scene.");
        var scene=EditorSceneManager.NewPreviewScene();
        GameObject root=null;
        try
        {
            root=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath),scene);root.SetActive(true);
            var cameraObject=new GameObject("SettingsPreviewCamera");SceneManager.MoveGameObjectToScene(cameraObject,scene);
            var camera=cameraObject.AddComponent<Camera>();camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=C("04060D");camera.orthographic=true;camera.nearClipPlane=.01f;camera.farClipPlane=100;camera.transform.position=new Vector3(0,0,-10);camera.scene=scene;
            var canvas=root.GetComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceCamera;canvas.worldCamera=camera;canvas.planeDistance=1;
            root.GetComponent<UnityEngine.UI.CanvasScaler>().enabled=false;
            var view=root.GetComponent<SettingsPanelView>();view.InitializeEditorPreview();
            Directory.CreateDirectory(Output);
            for(int tab=0;tab<5;tab++)
            {
                view.SelectTab(tab);view.Refresh();
                CaptureCamera(camera,1920,1080,Output+"/"+SettingsSchema.English[tab].ToLowerInvariant().Replace(" ","-")+".png");
            }
        }
        finally { EditorSceneManager.ClosePreviewScene(scene); }
        CaptureSizedPreview(1280,1024,Output+"/graphics-1280x1024.png");
        CaptureSizedPreview(2560,1080,Output+"/graphics-2560x1080.png");
    }
    private static void CaptureSizedPreview(int width,int height,string path)
    {
        var scene=EditorSceneManager.NewPreviewScene();var initialTarget=RenderTexture.GetTemporary(width,height,24,RenderTextureFormat.ARGB32);
        try
        {
            // A camera-sized canvas must be initialized at its target size. Reusing an already
            // rendered screen-space canvas across aspect changes leaves native clipping state stale.
            var cameraObject=new GameObject("SizedSettingsPreviewCamera");SceneManager.MoveGameObjectToScene(cameraObject,scene);
            var camera=cameraObject.AddComponent<Camera>();camera.orthographic=true;camera.nearClipPlane=.01f;camera.farClipPlane=100;camera.transform.position=new Vector3(0,0,-10);camera.scene=scene;camera.targetTexture=initialTarget;camera.aspect=(float)width/height;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=C("04060D");
            var root=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath),scene);root.SetActive(true);
            var canvas=root.GetComponent<Canvas>();root.GetComponent<UnityEngine.UI.CanvasScaler>().enabled=false;
            canvas.renderMode=RenderMode.ScreenSpaceCamera;canvas.worldCamera=camera;canvas.planeDistance=1;canvas.scaleFactor=Mathf.Min(width/1920f,height/1080f);
            root.GetComponent<SettingsPanelView>().InitializeEditorPreview();
            CaptureCamera(camera,width,height,path);
        }
        finally{EditorSceneManager.ClosePreviewScene(scene);RenderTexture.ReleaseTemporary(initialTarget);}
    }
    private static void CaptureCamera(Camera camera,int width,int height,string path)
    {
        var target=RenderTexture.GetTemporary(width,height,24,RenderTextureFormat.ARGB32);var before=RenderTexture.active;var previous=camera.targetTexture;Texture2D image=null;
        try
        {
            camera.targetTexture=target;camera.aspect=(float)width/height;
            foreach(var canvas in camera.scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<Canvas>()))canvas.scaleFactor=Mathf.Min(width/1920f,height/1080f);
            // Populate legacy dynamic-font glyphs before rebuilding all labels. A font atlas
            // can change during a one-frame preview when a different settings tab is shown.
            var texts=camera.scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<UnityEngine.UI.Text>()).ToArray();
            foreach(var text in texts)
                if(text.font!=null)
                {
                    string content=System.Text.RegularExpressions.Regex.Replace(text.text,"<[^>]*>","");
                    text.font.RequestCharactersInTexture(content,text.fontSize,text.fontStyle);
                    if(text.text.Contains("<size=10>"))text.font.RequestCharactersInTexture(content,10,text.fontStyle);
                }
            foreach(var graphic in camera.scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<UnityEngine.UI.Graphic>()))graphic.SetAllDirty();
            Canvas.ForceUpdateCanvases();
            foreach(var text in texts){text.cachedTextGenerator.Invalidate();text.SetAllDirty();}
            Canvas.ForceUpdateCanvases();
            var request=new RenderPipeline.StandardRequest{destination=target};
            if(GraphicsSettings.currentRenderPipeline!=null)RenderPipeline.SubmitRenderRequest(camera,request);else camera.Render();
            RenderTexture.active=target;image=new Texture2D(width,height,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,width,height),0,0);image.Apply();File.WriteAllBytes(path,image.EncodeToPNG());
        }
        finally{camera.targetTexture=previous;RenderTexture.active=before;if(image!=null)Object.DestroyImmediate(image);RenderTexture.ReleaseTemporary(target);}
    }

    private const string ValidationPrefix="FPS.Settings.Validation.";
    [InitializeOnLoadMethod]
    private static void RestoreValidationSceneOnReload()
    {
        EditorApplication.playModeStateChanged-=ValidationModeChanged;
        EditorApplication.playModeStateChanged+=ValidationModeChanged;
    }
    [MenuItem("Tools/FPS/Settings/Validate Settings Runtime (Offline)")]
    public static void ValidateRuntime()
    {
        Require(!EditorApplication.isPlayingOrWillChangePlaymode,"Stop Play Mode before validation.");
        Require(!SessionState.GetBool(ValidationPrefix+"active",false),"A settings validation is already active.");
        var scene=SceneManager.GetActiveScene();Require(!scene.isDirty,"Save the open scene before offline validation.");
        SessionState.SetString(ValidationPrefix+"scene",scene.path);
        SessionState.SetString(ValidationPrefix+"saved",PlayerPrefs.GetString(GameSettingsService.PreferenceKey,""));
        SessionState.SetString(ValidationPrefix+"time",PlayerPrefs.GetString(GameSettingsService.PreferenceKey+".time",""));
        SessionState.SetInt(ValidationPrefix+"quality",QualitySettings.GetQualityLevel());SessionState.SetInt(ValidationPrefix+"vsync",QualitySettings.vSyncCount);
        SessionState.SetInt(ValidationPrefix+"fps",Application.targetFrameRate);SessionState.SetFloat(ValidationPrefix+"volume",AudioListener.volume);
        SessionState.SetBool(ValidationPrefix+"active",true);
        var testScene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        var prefab=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath),testScene);prefab.SetActive(true);
        var events=new GameObject("EventSystem",typeof(UnityEngine.EventSystems.EventSystem),typeof(UnityEngine.InputSystem.UI.InputSystemUIInputModule));events.GetComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>().AssignDefaultActions();
        new GameObject("SettingsValidation",typeof(SettingsRuntimeValidationProbe));
        new GameObject("PreviewCamera",typeof(Camera),typeof(AudioListener));
        EditorSceneManager.SaveScene(testScene,Output+"/SettingsValidation.unity");
        EditorApplication.EnterPlaymode();
    }
    private static void ValidationModeChanged(PlayModeStateChange state)
    {
        if(state!=PlayModeStateChange.EnteredEditMode||!SessionState.GetBool(ValidationPrefix+"active",false))return;
        string saved=SessionState.GetString(ValidationPrefix+"saved","");string time=SessionState.GetString(ValidationPrefix+"time","");
        if(saved.Length>0)PlayerPrefs.SetString(GameSettingsService.PreferenceKey,saved);else PlayerPrefs.DeleteKey(GameSettingsService.PreferenceKey);
        if(time.Length>0)PlayerPrefs.SetString(GameSettingsService.PreferenceKey+".time",time);else PlayerPrefs.DeleteKey(GameSettingsService.PreferenceKey+".time");PlayerPrefs.Save();
        QualitySettings.SetQualityLevel(SessionState.GetInt(ValidationPrefix+"quality",2),true);QualitySettings.vSyncCount=SessionState.GetInt(ValidationPrefix+"vsync",0);Application.targetFrameRate=SessionState.GetInt(ValidationPrefix+"fps",-1);AudioListener.volume=SessionState.GetFloat(ValidationPrefix+"volume",1);
        string scene=SessionState.GetString(ValidationPrefix+"scene","");SessionState.SetBool(ValidationPrefix+"active",false);
        if(scene.Length>0)EditorSceneManager.OpenScene(scene,OpenSceneMode.Single);
        Debug.Log("[Settings] Offline validation finished; the original scene and local settings were restored.");
    }
}
