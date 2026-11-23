using System;
using System.IO;
using System.Linq;
using FPS.UI;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

public static class WardrobeBuilder
{
    private const string Home="Assets/Resource/UI/UnifiedLobby/UnifiedHome.prefab";
    private const string Ui="Assets/Resource/UI/Wardrobe";
    private const string Output="Assets/Resources/Customization/Wardrobe.prefab";
    private static TMP_FontAsset font;
    private static readonly Color Ink=WardrobeView.Ink, Blue=WardrobeView.Blue, Paper=WardrobeView.Paper;
    [MenuItem("Tools/FPS/Lobby/Build Wardrobe")]
    public static void Build()
    {
        if(EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("请先退出运行模式。");
        Directory.CreateDirectory(Ui); AssetDatabase.Refresh();
        var rig=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(WardrobeGeometryBuilder.Folder+"/RifleGirlBody.prefab"));
        GameObject canvas=null;
        try
        {
            WardrobeGeometryBuilder.Build(rig);
            var images=Thumbnails(rig,out var paletteAtlas);
            font=AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/Resource/UI/UnifiedLobby/Fonts/UnifiedLobbyBody.asset");
            canvas=new GameObject("Wardrobe",typeof(RectTransform),typeof(Canvas),typeof(CanvasScaler),typeof(GraphicRaycaster),typeof(CanvasGroup));
            var c=canvas.GetComponent<Canvas>(); c.renderMode=RenderMode.ScreenSpaceOverlay; c.sortingOrder=150;
            var scaler=canvas.GetComponent<CanvasScaler>(); scaler.uiScaleMode=CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution=new Vector2(1920,1080); scaler.matchWidthOrHeight=.5f;
            var v=canvas.AddComponent<WardrobeView>(); v.page=LobbyPage.Wardrobe; v.thumbnails=images; v.paletteAtlas=paletteAtlas;
            Design(v); canvas.SetActive(false);
            if(!PrefabUtility.SaveAsPrefabAsset(canvas,Output)) throw new IOException("Could not save wardrobe prefab.");
            UpdateHome(); UpdateWeapons(); AttachPage();
        }
        finally { if(canvas) Object.DestroyImmediate(canvas); Object.DestroyImmediate(rig); }
    }
    private static WardrobeView.ItemImages[] Thumbnails(GameObject rig,out Texture2D palette)
    {
        var result=new WardrobeView.ItemImages[WardrobeItems.Slots.Length];
        var app=rig.GetComponent<RifleGirlAppearance>();
        const int cell=96;
        int rows=WardrobeItems.PaletteRow(ClothingSlot.Accessory,1)+1;
        var atlas=new Texture2D(5*cell,rows*cell,TextureFormat.RGBA32,false);
        try
        {
        for(int s=0;s<result.Length;s++)
        {
            var slot=(ClothingSlot)s;
            var items=new Texture2D[WardrobeItems.Names[s].Length]; result[s].items=items;
            for(int i=0;i<items.Length;i++)
            {
                if(i==0 && WardrobeItems.Optional(slot)) continue;
                var a=CharacterAppearanceData.Default; a.wardrobeItems=WardrobeItems.Marker;
                a=WardrobeItems.Select(a,slot,i,0); app.Apply(a);
                var skin=app.WardrobeThumbnail(slot,i) as SkinnedMeshRenderer;
                if(!skin) throw new InvalidOperationException("Missing wardrobe renderer: "+slot);
                string path=Ui+"/"+slot+"-"+i+".png";
                int row=WardrobeItems.PaletteRow(slot,i);
                for(int dye=0;dye<(s==0?4:5);dye++)
                {
                    app.Apply(WardrobeItems.Select(a,slot,i,dye));
                    var chip=RenderThumb(skin,cell);
                    atlas.SetPixels(dye*cell,row*cell,cell,cell,chip.GetPixels()); Object.DestroyImmediate(chip);
                }
                app.Apply(a);
                var thumb=RenderThumb(skin,256); File.WriteAllBytes(path,thumb.EncodeToPNG()); Object.DestroyImmediate(thumb);
                AssetDatabase.ImportAsset(path);
                var importer=(TextureImporter)AssetImporter.GetAtPath(path);
                importer.alphaIsTransparency=true; importer.mipmapEnabled=false;
                importer.textureCompression=TextureImporterCompression.Uncompressed; importer.SaveAndReimport();
                items[i]=AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }
        }
        string atlasPath=Ui+"/ItemPalette.png";
        atlas.Apply(); File.WriteAllBytes(atlasPath,atlas.EncodeToPNG()); AssetDatabase.ImportAsset(atlasPath);
        var settings=(TextureImporter)AssetImporter.GetAtPath(atlasPath); settings.npotScale=TextureImporterNPOTScale.None;
        settings.mipmapEnabled=false; settings.alphaIsTransparency=true; settings.wrapMode=TextureWrapMode.Clamp;
        settings.textureCompression=TextureImporterCompression.Uncompressed; settings.SaveAndReimport();
        palette=AssetDatabase.LoadAssetAtPath<Texture2D>(atlasPath); return result;
        }
        finally { Object.DestroyImmediate(atlas); }
    }
    private static Texture2D RenderThumb(SkinnedMeshRenderer skin,int size)
    {
        var mesh=new Mesh(); var preview=new PreviewRenderUtility(); Texture2D tex=null; Material glovePreview=null;
        try
        {
            skin.BakeMesh(mesh);
            var world=skin.transform.localToWorldMatrix; var points=mesh.vertices; var indices=mesh.triangles;
            // Source character meshes contain unused vertices after part extraction.
            var bounds=new Bounds(world.MultiplyPoint3x4(points[indices[0]]),Vector3.zero);
            foreach(int i in indices) bounds.Encapsulate(world.MultiplyPoint3x4(points[i]));
            if(skin.name=="WardrobeGloves" || skin.name.Contains("Boots") || skin.name.Contains("Shoes"))
            {
                var center=bounds.center.x;
                var right=indices.Select(i=>world.MultiplyPoint3x4(points[i])).Where(p=>p.x>center).ToArray();
                if(right.Length>0) { bounds=new Bounds(right[0],Vector3.zero); foreach(var p in right) bounds.Encapsulate(p); }
            }
            var camera=preview.camera; camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=new Color(.87f,.92f,.985f,1);
            camera.orthographic=true; camera.orthographicSize=Mathf.Max(bounds.extents.y,bounds.extents.x)*1.16f+.005f;
            camera.nearClipPlane=.01f; camera.farClipPlane=20;
            camera.transform.position=bounds.center+new Vector3(.16f,.08f,3); camera.transform.LookAt(bounds.center);
            preview.ambientColor=Color.gray;
            preview.lights[0].intensity=1.2f; preview.lights[0].transform.rotation=Quaternion.Euler(28,155,0);
            preview.lights[1].intensity=.7f; preview.lights[1].transform.rotation=Quaternion.Euler(40,-40,0);
            preview.BeginStaticPreview(new Rect(0,0,size,size));
            var block=new MaterialPropertyBlock(); skin.GetPropertyBlock(block);
            for(int s=0;s<mesh.subMeshCount;s++)
            {
                Material material=skin.sharedMaterials[Mathf.Min(s,skin.sharedMaterials.Length-1)];
                // Static previews render with the built-in pipeline, although the game uses URP.
                if(skin.name=="WardrobeGloves")
                {
                    glovePreview=new Material(Shader.Find("Standard"));
                    glovePreview.color=block.isEmpty?material.GetColor("_BaseColor"):block.GetColor("_BaseColor");
                    material=glovePreview;
                }
                preview.DrawMesh(mesh,world,material,s,block.isEmpty?null:block);
            }
            preview.Render(); tex=preview.EndStaticPreview();
            var pixels=tex.GetPixels32(); var bg=pixels[0];
            for(int i=0;i<pixels.Length;i++)
            {
                var p=pixels[i];
                if(Mathf.Abs(p.r-bg.r)+Mathf.Abs(p.g-bg.g)+Mathf.Abs(p.b-bg.b)<10) p.a=0;
                pixels[i]=p;
            }
            var cutout=new Texture2D(tex.width,tex.height,TextureFormat.RGBA32,false);
            cutout.SetPixels32(pixels); cutout.Apply(); return cutout;
        }
        finally { preview.Cleanup(); if(glovePreview) Object.DestroyImmediate(glovePreview); if(tex) Object.DestroyImmediate(tex); Object.DestroyImmediate(mesh); }
    }
    private static void Design(WardrobeView v)
    {
        var bg=Panel(v.transform,"Backdrop",0,0,1,1,new Color32(207,224,245,255));
        bg.rectTransform.anchorMin=Vector2.zero; bg.rectTransform.anchorMax=Vector2.one;
        bg.rectTransform.offsetMin=bg.rectTransform.offsetMax=Vector2.zero;
        var stage=Rect(v.transform,"Stage",0,0,1920,1080);
        stage.anchorMin=stage.anchorMax=stage.pivot=new Vector2(.5f,.5f); stage.anchoredPosition=Vector2.zero;
        v.gameObject.AddComponent<UnifiedLobbyLayout>().stage=stage;
        var raw=Rect(stage,"Preview",1030,76,890,1004).gameObject.AddComponent<RawImage>();
        v.preview=raw.gameObject.AddComponent<WardrobePreview>(); v.preview.image=raw;
        v.preview.idle=AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>("Assets/Resource/UI/UnifiedLobby/HomepageIdle.controller");
        v.preview.riflePrefab=AssetDatabase.LoadAssetAtPath<GameObject>(RifleGirlAssetBuilder.SourceRoot+"/RifleGirl/Prefab/Prefab_Parts/Weapon_Rifle.prefab");
        v.preview.rifleIdle=AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Resource/Animations/RifleGirl/AimIdle.anim");
        var pvp=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/Players/PVP/RifleGirl_PVP_Net.prefab").GetComponent<PvpFullBodyController>();
        v.preview.eyeHeight=pvp.eyeHeight; v.preview.fieldOfView=pvp.fieldOfView;
        Panel(stage,"Header",0,0,1920,76,new Color32(24,69,153,255));
        var stripe=Panel(stage,"HeaderStripe",1800,-24,22,130,new Color32(54,112,198,255)); stripe.rectTransform.localRotation=Quaternion.Euler(0,0,-26);
        var end=Panel(stage,"SignalStripe",1877,-24,30,130,WardrobeView.Yellow); end.rectTransform.localRotation=Quaternion.Euler(0,0,-26);
        var back=Button(stage,"Back","‹",14,8,78,58,Blue,Color.white,45);
        Panel(stage,"TitleAccent",116,19,6,39,WardrobeView.Yellow); Label(stage,"Title","衣橱",144,0,380,76,47,Color.white);
        Panel(stage,"Rail",0,76,224,1004,new Color32(25,62,136,255));
        Panel(stage,"Inventory",224,76,810,1004,Paper);
        v.slots=new Button[8];
        for(int i=0;i<8;i++)
        {
            var b=Button(stage,"Slot"+i,WardrobeItems.Slots[i],8,104+i*108,208,87,Blue,Color.white,33);
            var txt=b.GetComponentInChildren<TMP_Text>(); txt.rectTransform.anchoredPosition=new Vector2(64,0); txt.rectTransform.sizeDelta=new Vector2(130,87);
            var slot=(ClothingSlot)i; var icon=Rect(b.transform,"Icon",12,22,45,45).gameObject.AddComponent<RawImage>();
            icon.texture=v.thumbnails[i].items[WardrobeItems.Optional(slot)?1:0]; icon.raycastTarget=false;
            Panel(b.transform,"Selected",0,0,6,87,WardrobeView.Yellow);
            v.slots[i]=b;
        }
        Panel(stage,"SectionAccent",256,112,7,39,WardrobeView.Yellow);
        v.heading=Label(stage,"Category","外套",280,90,440,82,45,Ink);
        v.cards=new WardrobeView.Card[3];
        for(int i=0;i<v.cards.Length;i++)
        {
            int x=252+i*253,y=190;
            var b=Button(stage,"Item"+i,"",x,y,241,354,Color.white,Ink,25);
            var border=b.image; var inner=Panel(b.transform,"Surface",3,3,235,348,new Color32(230,239,251,255));
            var icon=Rect(inner.transform,"Garment",5,34,224,227).gameObject.AddComponent<RawImage>(); icon.raycastTarget=false;
            var label=Label(inner.transform,"ItemName","",6,275,225,60,25,Ink,true);
            var badge=Panel(inner.transform,"State",131,8,96,31,Blue);
            var state=Label(badge.transform,"Caption","",0,0,96,31,19,Color.white,true);
            v.cards[i]=new WardrobeView.Card{button=b,border=border,icon=icon,name=label,badge=state};
        }
        Panel(stage,"SelectedItem",252,580,749,103,new Color32(221,234,250,255));
        v.selectedName=Label(stage,"SelectedName","",276,591,420,81,32,Ink);
        v.detail=Button(stage,"ItemDetail","单品详情",790,604,180,55,Color.white,Blue,25);
        Panel(stage,"Line",255,735,744,2,new Color32(184,206,237,255));
        v.innerIcon=Rect(stage,"InnerIcon",371,771,92,87).gameObject.AddComponent<RawImage>(); v.innerIcon.raycastTarget=false;
        v.outerIcon=Rect(stage,"OuterIcon",371,903,92,87).gameObject.AddComponent<RawImage>(); v.outerIcon.raycastTarget=false;
        Label(stage,"InnerCaption","内搭",264,785,100,62,24,Blue);
        v.inner=Label(stage,"Inner","",477,785,285,62,28,Ink);
        Label(stage,"OuterCaption","外层",264,913,100,62,24,Blue);
        v.outer=Label(stage,"Outer","",477,913,285,62,28,Ink);
        v.remove=Button(stage,"Remove","脱下外套",790,920,184,49,Color.white,Blue,24);
        v.removeText=v.remove.GetComponentInChildren<TMP_Text>();
        v.fullBody=Button(stage,"FullBody","全身",1058,99,114,53,Blue,Color.white,24);
        v.firstPerson=Button(stage,"FirstPerson","第一人称",1174,99,167,53,Paper,Ink,24);
        v.undo=Button(stage,"Undo","撤销试穿",1391,978,204,68,Paper,Ink,28);
        v.save=Button(stage,"Save","保存搭配",1615,978,269,68,WardrobeView.Yellow,Ink,32);
        var bevel=Rect(v.save.transform,"ActionFace",0,0,269,68).gameObject.AddComponent<FPS.Settings.SettingsShape>();
        bevel.bevel=13; bevel.color=WardrobeView.Yellow; bevel.raycastTarget=true; bevel.transform.SetAsFirstSibling();
        v.save.image.color=Color.clear; v.save.targetGraphic=bevel;
        v.feedback=Label(stage,"Feedback","",1055,887,815,64,24,Ink);
        v.dragHint=Label(stage,"DragHint","拖动旋转",1064,980,224,57,24,Ink);
        var d=Panel(stage,"ItemDetails",246,573,765,321,new Color32(220,232,252,255));
        d.raycastTarget=true; v.details=d.gameObject;
        v.detailTitle=Label(d.transform,"DetailTitle","",27,15,570,54,31,Ink);
        v.closeDetail=Button(d.transform,"Close","收起",621,17,119,47,Paper,Blue,21);
        Label(d.transform,"PaletteCaption","单品配色",27,86,329,45,24,Ink);
        v.colors=new Button[5];
        for(int i=0;i<v.colors.Length;i++)
        {
            v.colors[i]=Button(d.transform,"Color"+i,"",29+i*140,140,119,109,Color.white,Blue,16);
            var chip=Rect(v.colors[i].transform,"Sample",10,3,93,87).gameObject.AddComponent<RawImage>(); chip.raycastTarget=false;
            var caption=v.colors[i].GetComponentInChildren<TMP_Text>(); caption.rectTransform.anchoredPosition=new Vector2(0,-82);
            caption.rectTransform.sizeDelta=new Vector2(119,25);
        }
        d.gameObject.SetActive(false);
        v.buttons=new[]{new LobbyButtonBinding{button=back,action=LobbyAction.Back},new LobbyButtonBinding{button=v.save,action=LobbyAction.SaveProfile}};
    }
    private static RectTransform Rect(Transform parent,string name,float x,float y,float w,float h)
    {
        var r=new GameObject(name,typeof(RectTransform)).GetComponent<RectTransform>(); r.SetParent(parent,false);
        r.anchorMin=r.anchorMax=r.pivot=new Vector2(0,1); r.anchoredPosition=new Vector2(x,-y); r.sizeDelta=new Vector2(w,h);
        r.gameObject.layer=5; return r;
    }
    private static Image Panel(Transform p,string n,float x,float y,float w,float h,Color c)
    { var g=Rect(p,n,x,y,w,h).gameObject.AddComponent<Image>(); g.color=c; g.raycastTarget=false; return g; }
    private static TMP_Text Label(Transform p,string n,string s,float x,float y,float w,float h,int size,Color color,bool center=false)
    {
        var t=Rect(p,n,x,y,w,h).gameObject.AddComponent<TextMeshProUGUI>(); t.font=font; t.text=s; t.fontSize=size;
        t.color=color; t.enableWordWrapping=false; t.raycastTarget=false;
        t.alignment=center?TextAlignmentOptions.Center:TextAlignmentOptions.MidlineLeft; return t;
    }
    private static Button Button(Transform p,string n,string s,float x,float y,float w,float h,Color fill,Color ink,int size)
    {
        var face=Panel(p,n,x,y,w,h,fill); face.raycastTarget=true; var b=face.gameObject.AddComponent<Button>(); b.targetGraphic=face;
        var state=b.colors; state.highlightedColor=new Color(.92f,.95f,1); state.disabledColor=new Color(.83f,.85f,.88f,.85f); b.colors=state;
        if(s.Length>0 || n.StartsWith("Color")) Label(face.transform,"Label",s,0,0,w,h,size,ink,true);
        return b;
    }
    [MenuItem("Tools/FPS/Lobby/Attach Wardrobe to GameLobby")]
    public static void AttachPage()
    {
        var root=Object.FindObjectOfType<LobbyUIRoot>(true);
        if(!root || root.gameObject.scene.path!="Assets/Scenes/GameLobby.unity")
            throw new InvalidOperationException("请先打开 GameLobby 场景。");
        if(root.pages.Any(p=>p is WardrobeView)) return;
        var scene=root.gameObject.scene;
        if(scene.isDirty)
        {
            Directory.CreateDirectory("tmp/WardrobeV3");
            string backup=Path.GetFullPath("tmp/WardrobeV3/GameLobby-before-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".unity");
            if(!UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene,backup,true)) throw new IOException("无法备份打开的大厅。");
        }
        var go=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Output),root.customizationView.transform.parent);
        var pages=root.pages.ToList(); pages.Add(go.GetComponent<WardrobeView>()); root.pages=pages.ToArray();
        EditorUtility.SetDirty(root);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
        if(!UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene)) throw new IOException("无法保存衣橱的场景接线。");
    }
    private static void UpdateHome()
    {
        var root=PrefabUtility.LoadPrefabContents(Home);
        try
        {
            var view=root.GetComponent<UnifiedLobbyView>();
            var obsolete=view.buttons.FirstOrDefault(b=>b.action==LobbyAction.Character);
            if(obsolete!=null)
            {
                view.buttons=view.buttons.Where(b=>b.action!=LobbyAction.Character).ToArray();
                Object.DestroyImmediate(obsolete.button.gameObject);
            }
            foreach(var b in view.buttons.Where(b=>b.action==LobbyAction.Clothes || b.action==LobbyAction.Loadout))
            {
                var r=(RectTransform)b.button.transform;
                r.anchoredPosition=new Vector2(b.action==LobbyAction.Clothes?730:974,r.anchoredPosition.y);
            }
            PrefabUtility.SaveAsPrefabAsset(root,Home);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }
    private static void UpdateWeapons()
    {
        const string path="Assets/Resource/UI/Prefab/Mvc/CustomizationPanel.prefab";
        var root=PrefabUtility.LoadPrefabContents(path);
        try
        {
            root.transform.Find("Title").GetComponent<Text>().text="武器配装";
            var options=root.transform.Find("Options");
            foreach(Transform t in options)
            {
                if(t.name=="AppearanceTitle" || (t.name.StartsWith("Choice") && t.name[6]<'6')) t.gameObject.SetActive(false);
                else if(t.name=="LoadoutTitle" || t.name.StartsWith("Choice"))
                {
                    var r=(RectTransform)t;
                    int index=t.name.StartsWith("Choice")?t.name[6]-'6':-1;
                    r.anchoredPosition=new Vector2(r.anchoredPosition.x,-(index<0?22:78+index*58));
                }
            }
            PrefabUtility.SaveAsPrefabAsset(root,path);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }
}
