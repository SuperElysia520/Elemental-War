using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations.Rigging;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/// <summary>只给已经打开的 GameStart 接入账号展示角色；不会创建或打开大厅场景。</summary>
public static class MainMenuCharacterSetup
{
    private const string ScenePath = "Assets/Scenes/GameStart.unity";
    private const string BodyPath = "Assets/Resources/Customization/RifleGirlBody.prefab";
    private const string ControllerPath = "Assets/Resources/Customization/RifleGirlLobby.controller";
    private static readonly string OutputDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "../tmp/MainMenuFlow"));

    [MenuItem("Tools/FPS/Main Menu/Bind Account Character")]
    public static void Apply()
    {
        Scene scene = RequireOpenMainMenu();
        PreserveSceneCopy(scene);

        AccountCharacterPreview[] previews = SceneComponents<AccountCharacterPreview>(scene);
        RifleGirlAppearance[] appearances = SceneComponents<RifleGirlAppearance>(scene);
        Require(previews.Length <= 1, "GameStart 中存在多个 AccountCharacterPreview，请先明确唯一的菜单展示角色。");
        Require(appearances.Length <= 1, "GameStart 中存在多个 RifleGirlAppearance，请先明确唯一的菜单展示角色。");

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Bind Main Menu Account Character");
        try
        {
            if (appearances.Length == 1)
            {
                RifleGirlAppearance existing = appearances[0];
                RequirePresentationObject(existing.gameObject);
                AccountCharacterPreview preview = previews.Length == 1
                    ? previews[0]
                    : Undo.AddComponent<AccountCharacterPreview>(existing.gameObject);
                Bind(preview, existing);
            }
            else
            {
                Require(previews.Length == 0, "现有 AccountCharacterPreview 没有 RifleGirl 展示对象，请先在 Inspector 修复引用。");
                AddDisplayCharacter(scene);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            Require(EditorSceneManager.SaveScene(scene), "无法保存当前 GameStart 场景，场景改动已保留在编辑器中。");
            Undo.CollapseUndoOperations(undoGroup);
            Debug.Log("[MainMenuCharacterSetup] 已绑定账号外观；修改前快照：" + Path.Combine(OutputDirectory, "main-menu-before.unity"));
        }
        catch
        {
            Undo.RevertAllDownToGroup(undoGroup);
            throw;
        }
    }

    private static void AddDisplayCharacter(Scene scene)
    {
        GameObject source = FindLegacyDisplay(scene);
        RequirePresentationObject(source);
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(BodyPath);
        RuntimeAnimatorController controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ControllerPath);
        Require(prefab != null && controller != null, "缺少已准备好的 RifleGirlBody prefab 或 RifleGirlLobby controller。");

        Animator sourceAnimator = source.GetComponent<Animator>();
        Undo.RegisterFullObjectHierarchyUndo(source, "Preserve Original Menu Character");
        if (sourceAnimator != null && sourceAnimator.runtimeAnimatorController != null)
            sourceAnimator.Update(0f);
        Transform sourceHead = FindSourceHead(source, sourceAnimator);
        Require(sourceHead != null, "原菜单角色缺少可识别的头部骨骼，未替换角色。");
        Vector3 headPosition = sourceHead.position;

        GameObject display = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        Require(display != null, "无法在当前 GameStart 中创建 RifleGirl prefab 实例。");
        Undo.RegisterCreatedObjectUndo(display, "Add Main Menu RifleGirl");
        display.name = "MainMenu_RifleGirl";
        display.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
        display.transform.localScale = source.transform.localScale;
        display.SetActive(true);

        Animator animator = display.GetComponent<Animator>();
        RifleGirlAppearance appearance = display.GetComponent<RifleGirlAppearance>();
        Require(animator != null && animator.avatar != null && animator.avatar.isHuman && appearance != null,
            "RifleGirl prefab 缺少有效 Humanoid Animator 或 RifleGirlAppearance。");
        animator.runtimeAnimatorController = controller;
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        animator.Rebind();
        animator.Update(0f);
        Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
        Require(head != null, "RifleGirl prefab 未映射 Head 骨骼。");
        display.transform.position += headPosition - head.position;
        appearance.SetLocalFirstPerson(false);
        appearance.Apply(CharacterAppearanceData.Default);
        PrefabUtility.RecordPrefabInstancePropertyModifications(display);
        PrefabUtility.RecordPrefabInstancePropertyModifications(animator);
        PrefabUtility.RecordPrefabInstancePropertyModifications(display.transform);

        Bind(Undo.AddComponent<AccountCharacterPreview>(display), appearance);

        // 原展示模型中还带有服装插件和武器组件，保留完整对象以免丢失用户引用。
        Undo.RecordObject(source, "Disable Legacy Menu Character");
        source.SetActive(false);
        source.name = "Legacy_Menu_Lumine";
        PrefabUtility.RecordPrefabInstancePropertyModifications(source);
        Debug.LogWarning("[MainMenuCharacterSetup] 原 Lumine 展示对象已禁用并保留。独立 HeadAimTarget 保留；旧头骨约束不会驱动 RifleGirl，新角色的鼠标转头尚未接线。");
    }

    private static Transform FindSourceHead(GameObject source, Animator animator)
    {
        if (animator != null && animator.avatar != null && animator.avatar.isValid && animator.avatar.isHuman)
            return animator.GetBoneTransform(HumanBodyBones.Head);

        // 当前 Lumine 展示 Animator 没有 Avatar，使用场景已配置的头部约束引用。
        foreach (MultiAimConstraint constraint in source.GetComponentsInChildren<MultiAimConstraint>(true))
            if (constraint.name == "HeadAimConstraint" && constraint.data.constrainedObject != null)
                return constraint.data.constrainedObject;
        return null;
    }

    private static GameObject FindLegacyDisplay(Scene scene)
    {
        GameObject found = null;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.name != "Lumine" && root.name != "Legacy_Menu_Lumine") continue;
            Require(found == null, "GameStart 存在多个旧菜单角色，未选择替换对象。");
            found = root;
        }
        Require(found != null, "当前 GameStart 没有原 Lumine 展示根对象；请手工摆入 RifleGirl 并添加 AccountCharacterPreview。");
        return found;
    }

    private static void RequirePresentationObject(GameObject root)
    {
        Require(root.GetComponentsInChildren<Renderer>(true).Length != 0, "目标对象不是带网格的展示角色。");
        Require(root.GetComponentsInChildren<Camera>(true).Length == 0 &&
            root.GetComponentsInChildren<Canvas>(true).Length == 0 &&
            root.GetComponentsInChildren<AudioListener>(true).Length == 0 &&
            root.GetComponentsInChildren<Unity.Netcode.NetworkObject>(true).Length == 0 &&
            root.GetComponentsInChildren<PlayerController>(true).Length == 0 &&
            root.GetComponentsInChildren<PlayerModel>(true).Length == 0,
            "目标对象包含相机、界面或玩家/网络逻辑，不会将它作为菜单展示对象替换。");
    }

    private static void Bind(AccountCharacterPreview preview, RifleGirlAppearance appearance)
    {
        SerializedObject serialized = new SerializedObject(preview);
        SerializedProperty target = serialized.FindProperty("appearance");
        Require(target != null, "AccountCharacterPreview 的 appearance 字段不可用。");
        if (target.objectReferenceValue == appearance) return;
        Undo.RecordObject(preview, "Bind Saved Character Appearance");
        target.objectReferenceValue = appearance;
        serialized.ApplyModifiedProperties();
        PrefabUtility.RecordPrefabInstancePropertyModifications(preview);
    }

    private static void PreserveSceneCopy(Scene scene)
    {
        Directory.CreateDirectory(OutputDirectory);
        bool wasDirty = scene.isDirty;
        string path = Path.Combine(OutputDirectory, "main-menu-before.unity");
        // saveAsCopy 保留当前打开的 Scene 对象及原路径，不会重载或丢弃未保存内容。
        bool saved = EditorSceneManager.SaveScene(scene, path, true);
        if (wasDirty && !scene.isDirty) EditorSceneManager.MarkSceneDirty(scene);
        Require(saved, "无法保存主菜单修改前快照，未开始接线。");
    }

    [MenuItem("Tools/FPS/Main Menu/Capture Current Menu Preview")]
    public static void CapturePreview()
    {
        CaptureCurrentMenu(RequireOpenMainMenu(), "main-menu-preview.png");
        Debug.Log("[MainMenuCharacterSetup] 编辑器相机预览仅用于角色构图；UI 视觉检查请使用 Play Mode 的完整 GameView 截图。");
    }

    public static void CaptureRuntimePreview()
    {
        Require(EditorApplication.isPlaying, "运行时主菜单截图需要 Play Mode。");
        Scene scene = RequireOpenMainMenu(true);
        Require(Screen.width > 0 && Screen.height > 0, "GameView 没有有效的绘制尺寸。");
        Directory.CreateDirectory(OutputDirectory);
        RenderTexture target = RenderTexture.GetTemporary(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32);
        RenderTexture previousActive = RenderTexture.active;
        Texture2D image = null;
        try
        {
            // 调用方须在 WaitForEndOfFrame 后进入：读取正常 GameView 最终画面，包含全部相机与 UI。
            // 不使用额外 Camera.Render，不改 Canvas.worldCamera，也不重新合成截图专用画面。
            ScreenCapture.CaptureScreenshotIntoRenderTexture(target);
            RenderTexture.active = target;
            image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            if (SystemInfo.graphicsUVStartsAtTop)
            {
                // D3D 的屏幕捕获 RT 行序与 PNG 纹理行序相反，只交换上下行，保持 X 方向。
                Color32[] pixels = image.GetPixels32();
                for (int y = 0; y < target.height / 2; y++)
                {
                    int top = y * target.width;
                    int bottom = (target.height - 1 - y) * target.width;
                    for (int x = 0; x < target.width; x++)
                    {
                        Color32 pixel = pixels[top + x];
                        pixels[top + x] = pixels[bottom + x];
                        pixels[bottom + x] = pixel;
                    }
                }
                image.SetPixels32(pixels);
            }
            image.Apply();
            File.WriteAllBytes(Path.Combine(OutputDirectory, "main-menu-runtime-preview.png"), image.EncodeToPNG());
            WriteRuntimeUiDiagnostics(scene);
        }
        finally
        {
            RenderTexture.active = previousActive;
            if (image != null) Object.DestroyImmediate(image);
            RenderTexture.ReleaseTemporary(target);
        }
        Debug.Log("[MainMenuCharacterSetup] 实际运行 GameView 截图：" + Path.Combine(OutputDirectory, "main-menu-runtime-preview.png"));
    }

    [Serializable] private sealed class RuntimeUiDiagnostics
    {
        public int width, height;
        public CameraDiagnostic[] cameras;
        public CanvasDiagnostic[] canvases;
    }

    [Serializable] private sealed class CameraDiagnostic
    {
        public string name, renderType;
        public bool active, enabled, rendersToTexture;
        public float depth;
        public int cullingMask;
        public string[] stack;
    }

    [Serializable] private sealed class CanvasDiagnostic
    {
        public string name, renderMode, camera;
        public bool active, enabled;
        public float planeDistance;
        public GraphicDiagnostic[] graphics;
        public CanvasGroupDiagnostic[] groups;
    }

    [Serializable] private sealed class GraphicDiagnostic
    {
        public string name;
        public bool active, enabled, culled;
        public float colorAlpha, rendererAlpha, inheritedAlpha;
        public Vector3 viewportCenter;
    }

    [Serializable] private sealed class CanvasGroupDiagnostic
    {
        public string name;
        public bool active, enabled;
        public float alpha;
    }

    private static void WriteRuntimeUiDiagnostics(Scene scene)
    {
        var cameras = new List<CameraDiagnostic>();
        foreach (Camera camera in SceneComponents<Camera>(scene))
        {
            var stack = new List<string>();
            UniversalAdditionalCameraData data = camera.GetComponent<UniversalAdditionalCameraData>();
            if (data != null && data.renderType == CameraRenderType.Base)
                foreach (Camera overlay in data.cameraStack)
                    if (overlay != null) stack.Add(overlay.name);
            cameras.Add(new CameraDiagnostic
            {
                name = camera.name, active = camera.gameObject.activeInHierarchy, enabled = camera.enabled,
                renderType = data != null ? data.renderType.ToString() : "BuiltIn",
                rendersToTexture = camera.targetTexture != null, depth = camera.depth,
                cullingMask = camera.cullingMask, stack = stack.ToArray()
            });
        }
        var canvases = new List<CanvasDiagnostic>();
        foreach (Canvas canvas in SceneComponents<Canvas>(scene))
        {
            var graphics = new List<GraphicDiagnostic>();
            foreach (Graphic graphic in canvas.GetComponentsInChildren<Graphic>(true))
            {
                if (graphic.canvas != canvas) continue;
                Vector3 viewport = canvas.worldCamera != null
                    ? canvas.worldCamera.WorldToViewportPoint(graphic.transform.position)
                    : new Vector3(graphic.transform.position.x / Screen.width, graphic.transform.position.y / Screen.height, 0f);
                graphics.Add(new GraphicDiagnostic
                {
                    name = graphic.name, active = graphic.gameObject.activeInHierarchy, enabled = graphic.enabled,
                    culled = graphic.canvasRenderer.cull, colorAlpha = graphic.color.a,
                    rendererAlpha = graphic.canvasRenderer.GetAlpha(), inheritedAlpha = graphic.canvasRenderer.GetInheritedAlpha(),
                    viewportCenter = viewport
                });
            }
            var groups = new List<CanvasGroupDiagnostic>();
            foreach (CanvasGroup group in canvas.GetComponentsInChildren<CanvasGroup>(true))
                groups.Add(new CanvasGroupDiagnostic
                {
                    name = group.name, active = group.gameObject.activeInHierarchy, enabled = group.enabled, alpha = group.alpha
                });
            canvases.Add(new CanvasDiagnostic
            {
                name = canvas.name, active = canvas.gameObject.activeInHierarchy, enabled = canvas.enabled,
                renderMode = canvas.renderMode.ToString(), camera = canvas.worldCamera != null ? canvas.worldCamera.name : "None",
                planeDistance = canvas.planeDistance, graphics = graphics.ToArray(), groups = groups.ToArray()
            });
        }
        var diagnostics = new RuntimeUiDiagnostics
        {
            width = Screen.width, height = Screen.height, cameras = cameras.ToArray(), canvases = canvases.ToArray()
        };
        File.WriteAllText(Path.Combine(OutputDirectory, "runtime-ui-diagnostics.json"), JsonUtility.ToJson(diagnostics, true));
    }

    private static void CaptureCurrentMenu(Scene scene, string outputFile)
    {
        Camera camera = null;
        foreach (Camera candidate in SceneComponents<Camera>(scene))
            if (candidate.enabled && candidate.gameObject.activeInHierarchy && candidate.CompareTag("MainCamera"))
            {
                Require(camera == null, "当前 GameStart 中存在多台活动 MainCamera。");
                camera = candidate;
            }
        Require(camera != null, "当前 GameStart 没有活动 MainCamera。");

        UniversalAdditionalCameraData cameraData = camera.GetComponent<UniversalAdditionalCameraData>();
        Camera uiCamera = camera;
        if (cameraData != null && cameraData.renderType == CameraRenderType.Base)
            foreach (Camera stacked in cameraData.cameraStack)
                if (stacked != null && stacked.enabled && stacked.gameObject.activeInHierarchy) uiCamera = stacked;

        Directory.CreateDirectory(OutputDirectory);
        RenderTexture target = RenderTexture.GetTemporary(1600, 900, 24, RenderTextureFormat.ARGB32);
        RenderTexture previousActive = RenderTexture.active;
        RenderTexture previousTarget = camera.targetTexture;
        Texture2D image = null;
        var overlays = new List<OverlayCanvasState>();
        try
        {
            // Camera stack 已包含现有 UICamera；临时转换活动 Overlay Canvas 让截图也包含它们。
            foreach (Canvas canvas in SceneComponents<Canvas>(scene))
                if (canvas.enabled && canvas.gameObject.activeInHierarchy && canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                {
                    overlays.Add(new OverlayCanvasState(canvas));
                    canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    canvas.worldCamera = uiCamera;
                    canvas.planeDistance = Mathf.Clamp(1f, uiCamera.nearClipPlane + 0.01f, uiCamera.farClipPlane - 0.01f);
                }
            Canvas.ForceUpdateCanvases();

            var request = new RenderPipeline.StandardRequest { destination = target };
            if (GraphicsSettings.currentRenderPipeline != null)
            {
                Require(RenderPipeline.SupportsRenderRequest(camera, request), "当前渲染管线不支持包含 UICamera 的截图请求。");
                RenderPipeline.SubmitRenderRequest(camera, request);
            }
            else
            {
                camera.targetTexture = target;
                camera.Render();
            }
            RenderTexture.active = target;
            image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            image.Apply();
            File.WriteAllBytes(Path.Combine(OutputDirectory, outputFile), image.EncodeToPNG());
        }
        finally
        {
            camera.targetTexture = previousTarget;
            foreach (OverlayCanvasState overlay in overlays) overlay.Restore();
            Canvas.ForceUpdateCanvases();
            RenderTexture.active = previousActive;
            if (image != null) Object.DestroyImmediate(image);
            RenderTexture.ReleaseTemporary(target);
        }
        Debug.Log("[MainMenuCharacterSetup] 主菜单与相机堆栈截图：" + Path.Combine(OutputDirectory, outputFile));
    }

    private sealed class OverlayCanvasState
    {
        private readonly Canvas canvas;
        private readonly Camera camera;
        private readonly float distance;
        private readonly Vector2 anchorMin, anchorMax, pivot, size;
        private readonly Vector3 position, scale;
        private readonly Quaternion rotation;

        public OverlayCanvasState(Canvas value)
        {
            canvas = value;
            camera = value.worldCamera;
            distance = value.planeDistance;
            RectTransform rect = (RectTransform)value.transform;
            anchorMin = rect.anchorMin; anchorMax = rect.anchorMax; pivot = rect.pivot; size = rect.sizeDelta;
            position = rect.localPosition; rotation = rect.localRotation; scale = rect.localScale;
        }

        public void Restore()
        {
            if (canvas == null) return;
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.worldCamera = camera;
            canvas.planeDistance = distance;
            RectTransform rect = (RectTransform)canvas.transform;
            rect.anchorMin = anchorMin; rect.anchorMax = anchorMax; rect.pivot = pivot; rect.sizeDelta = size;
            rect.localPosition = position; rect.localRotation = rotation; rect.localScale = scale;
        }
    }

    private static Scene RequireOpenMainMenu(bool allowPlayMode = false)
    {
        Require(allowPlayMode || !EditorApplication.isPlayingOrWillChangePlaymode, "主菜单接线与截图需要 Edit Mode。");
        Scene scene = SceneManager.GetSceneByPath(ScenePath);
        Require(scene.IsValid() && scene.isLoaded, "请先手工打开 GameStart；此工具不会自动打开或重载场景。");
        return scene;
    }

    private static T[] SceneComponents<T>(Scene scene) where T : Component
    {
        var result = new List<T>();
        foreach (GameObject root in scene.GetRootGameObjects()) result.AddRange(root.GetComponentsInChildren<T>(true));
        return result.ToArray();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
