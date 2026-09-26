using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using Object = UnityEngine.Object;

/// <summary>生成项目拥有的 RifleGirl 基础角色；不修改演示源 prefab，也不替换网络玩家。</summary>
public static class RifleGirlAssetBuilder
{
    public const string SourceRoot = "Assets/CombatGirlsCharacterPack";
    public const string OutputRoot = "Assets/Resources/Customization";
    public const string BodyPrefabPath = OutputRoot + "/RifleGirlBody.prefab";
    public const string ControllerPath = OutputRoot + "/RifleGirl.controller";
    public const string LobbyControllerPath = OutputRoot + "/RifleGirlLobby.controller";
    public const string UpperBodyLayer = "UpperBody";
    public const int UpperBodyLayerIndex = 1;
    public const string HandsIkLayer = "HandsIK";

    private const string AnimationRoot = SourceRoot + "/RifleGirl/Animations/";
    private const string GeneratedAnimationRoot = "Assets/Resource/Animations/RifleGirl";
    private const string SourcePrefab = SourceRoot + "/RifleGirl/Prefab/Rifle_Full_Body.prefab";
    private const string AvatarPath = SourceRoot + "/Humanoid_Bot/Models/Humanoid_F.fbx";
    private const string MagicaClothGuid = "bdbd3ce05f5b45942b56ede5c9b38364";
    private static readonly string[] BodyRendererPaths = { "Jakcket/Body_Jacke", "Dress/Body_Full", "Sportswear/Body_Sportswea" };

    [Serializable] private sealed class HeadVisibilityReport { public HeadVisibilityEntry[] bodies; }
    [Serializable] private sealed class HeadVisibilityEntry
    {
        public string body, source, firstPersonTemplate;
        public int vertices, sourceTriangles, hiddenTriangles;
        public float headWeightThreshold = 0.5f;
    }

    [Serializable]
    private sealed class BodyBuildReport
    {
        public bool dynamicClothAvailable;
        public int removedClothHelpers;
        public string note;
    }

    [Serializable] private sealed class SkinningReport { public SkinningEntry[] renderers; }
    [Serializable] private sealed class SkinningEntry
    {
        public string prefab, renderer, shader;
        public int boneCount, bindPoseCount, canonicalBoneCount;
        public string[] boneOrderDifferences;
        public SkinningBone[] bones;
    }
    [Serializable] private sealed class SkinningBone
    {
        public int index;
        public string name, canonicalName;
        public float bindMatrixError, vertexWeightSum;
    }

    public static void InspectSkinning()
    {
        GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(SourceRoot + "/RifleGirl/Models/Rifle_Full_Body.FBX");
        if (model == null) throw new InvalidOperationException("RifleGirl source model is missing.");
        SkinnedMeshRenderer[] canonical = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        var entries = new List<SkinningEntry>();
        foreach (string path in new[] { SourcePrefab, BodyPrefabPath })
        {
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (!renderer.name.StartsWith("Body_", StringComparison.Ordinal) && !renderer.name.Contains("Boots") && !renderer.name.Contains("Shoes")) continue;
                    SkinnedMeshRenderer expected = null;
                    foreach (SkinnedMeshRenderer candidate in canonical)
                        if (candidate.name == renderer.name || candidate.name + " (1)" == renderer.name) { expected = candidate; break; }
                    var differences = new List<string>();
                    var bones = new List<SkinningBone>();
                    Mesh mesh = renderer.sharedMesh;
                    Matrix4x4[] bind = mesh.bindposes;
                    Transform[] actualBones = renderer.bones;
                    Transform[] expectedBones = expected != null ? expected.bones : Array.Empty<Transform>();
                    var sums = new float[actualBones.Length];
                    foreach (BoneWeight weight in mesh.boneWeights)
                    {
                        if (weight.boneIndex0 < sums.Length) sums[weight.boneIndex0] += weight.weight0;
                        if (weight.boneIndex1 < sums.Length) sums[weight.boneIndex1] += weight.weight1;
                        if (weight.boneIndex2 < sums.Length) sums[weight.boneIndex2] += weight.weight2;
                        if (weight.boneIndex3 < sums.Length) sums[weight.boneIndex3] += weight.weight3;
                    }
                    for (int i = 0; i < actualBones.Length; i++)
                    {
                        string actual = actualBones[i] != null ? actualBones[i].name : "NULL";
                        string expectedName = i < expectedBones.Length && expectedBones[i] != null ? expectedBones[i].name : "UNAVAILABLE";
                        if (actual != expectedName) differences.Add(i + ": " + actual + " -> " + expectedName);
                        float error = 0f;
                        if (i < bind.Length && actualBones[i] != null)
                        {
                            Matrix4x4 matrix = renderer.transform.worldToLocalMatrix * actualBones[i].localToWorldMatrix * bind[i];
                            for (int element = 0; element < 16; element++) error = Mathf.Max(error, Mathf.Abs(matrix[element] - Matrix4x4.identity[element]));
                        }
                        bones.Add(new SkinningBone { index = i, name = actual, canonicalName = expectedName,
                            bindMatrixError = error, vertexWeightSum = sums[i] });
                    }
                    entries.Add(new SkinningEntry { prefab = path, renderer = renderer.name,
                        shader = renderer.sharedMaterial != null ? renderer.sharedMaterial.shader.name : "MISSING",
                        boneCount = actualBones.Length, bindPoseCount = bind.Length, canonicalBoneCount = expectedBones.Length,
                        boneOrderDifferences = differences.ToArray(), bones = bones.ToArray() });
                }
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
        string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../tmp/PvpMigration"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "skinning-diagnostic.json"), JsonUtility.ToJson(new SkinningReport { renderers = entries.ToArray() }, true));
        Debug.Log("[RifleGirl] Read-only skin binding diagnostic completed.");
    }

    [MenuItem("Tools/FPS/Build RifleGirl Body Assets")]
    public static void Build()
    {
        EnsureFolder(OutputRoot);
        EnsureFolder(GeneratedAnimationRoot);
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        Avatar avatar = LoadAvatar();
        RepairAnimationAvatars(avatar);
        AnimatorController controller = BuildController();
        BuildLobbyPreview();

        GameObject root = PrefabUtility.LoadPrefabContents(SourcePrefab);
        BodyBuildReport report;
        try
        {
            // 生成独立基础 prefab，防止升级源资源时恢复演示脚本或包内枪械。
            report = RemoveUnavailableClothHelpers(root);
            RemoveDemoComponentsAndWeapon(root);
            root.name = "RifleGirlBody";
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            root.transform.localScale = Vector3.one;

            Animator animator = root.GetComponent<Animator>();
            if (animator == null)
                animator = root.AddComponent<Animator>();
            animator.avatar = avatar;
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            RifleGirlAppearance appearance = root.GetComponent<RifleGirlAppearance>();
            if (appearance == null)
                appearance = root.AddComponent<RifleGirlAppearance>();
            ConfigureAppearance(root.transform, animator, appearance, BuildFirstPersonHeadTemplates(root, animator));

            // 骨骼由整个身体和衣装共享；无相机附属手臂、无第二个 Animator。
            if (root.GetComponentsInChildren<Animator>(true).Length != 1)
                throw new InvalidOperationException("RifleGirlBody must contain exactly one Animator.");

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, BodyPrefabPath, out bool savedSuccessfully);
            if (!savedSuccessfully || saved == null)
                throw new InvalidOperationException("Failed to save RifleGirl body prefab: " + BodyPrefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        AssetDatabase.SaveAssets();
        string reportDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "../tmp/PvpMigration"));
        Directory.CreateDirectory(reportDirectory);
        File.WriteAllText(Path.Combine(reportDirectory, "riflegirl-body-build.json"), JsonUtility.ToJson(report, true));
        Debug.Log("[RifleGirl] Created body prefab and controller: " + BodyPrefabPath);
    }

    [MenuItem("Tools/FPS/Build RifleGirl Lobby Preview")]
    public static void BuildLobbyPreview()
    {
        EnsureFolder(OutputRoot);
        EnsureFolder(GeneratedAnimationRoot);
        AnimationClip idle = CopyClip("LobbyPreviewIdle", AnimationRoot + "Normal/R_Idle.fbx", true);
        AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(LobbyControllerPath);
        if (controller == null)
        {
            controller = new AnimatorController { name = "RifleGirlLobby" };
            AssetDatabase.CreateAsset(controller, LobbyControllerPath);
        }
        else
        {
            controller.layers = Array.Empty<AnimatorControllerLayer>();
            controller.parameters = Array.Empty<AnimatorControllerParameter>();
            foreach (Object subAsset in AssetDatabase.LoadAllAssetsAtPath(LobbyControllerPath))
                if (subAsset != null && subAsset != controller) Object.DestroyImmediate(subAsset, true);
        }
        AnimatorStateMachine machine = AddLayer(controller, "Base Layer", null, false);
        machine.defaultState = AddState(machine, "Idle", idle, new Vector3(240, 50));
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        Debug.Log("[RifleGirl] Built separate lobby idle controller; gameplay animation controller is unchanged.");
    }

    private static BodyBuildReport RemoveUnavailableClothHelpers(GameObject root)
    {
        // The installed Magica package requires Collections >= 1.4. Its assembly is
        // excluded with this project's 1.2.4 package, even though each MonoScript GUID
        // exists. Unity does not classify these components as missing scripts and
        // refuses to save them. Never change the project's package versions here.
        string scriptPath = AssetDatabase.GUIDToAssetPath(MagicaClothGuid);
        MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
        Type clothType = script != null ? script.GetClass() : null;
        bool available = clothType != null && typeof(MonoBehaviour).IsAssignableFrom(clothType);
        var report = new BodyBuildReport { dynamicClothAvailable = available };
        if (available)
        {
            report.note = "Magica Cloth types are available; authored cloth simulation is preserved.";
            return report;
        }

        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            if (child == null || !child.name.StartsWith("Magica ", StringComparison.Ordinal)) continue;
            // These are dedicated simulation helpers, not character bones or meshes.
            // Stop if the vendor hierarchy changes instead of deleting model content.
            if (child.childCount != 0 || child.GetComponent<Renderer>() != null || child.GetComponent<Animator>() != null)
                throw new InvalidOperationException("Cannot strip non-leaf cloth helper safely: " + child.name);
            Object.DestroyImmediate(child.gameObject);
            report.removedClothHelpers++;
        }
        report.note = "Dynamic cloth/hair simulation is unavailable: the installed Magica assembly requires Collections >= 1.4.0, " +
            "while this project uses 1.2.4. Generated RifleGirl retains every clothing/hair mesh and the shared animated skeleton; " +
            "only dedicated cloth simulation helpers were removed. Source assets and package versions were preserved.";
        Debug.LogWarning("[RifleGirl] " + report.note + " Removed helpers: " + report.removedClothHelpers);
        return report;
    }

    private static Avatar LoadAvatar()
    {
        foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(AvatarPath))
        {
            if (asset is Avatar avatar && avatar.isValid && avatar.isHuman)
                return avatar;
        }
        throw new InvalidOperationException("RifleGirl Humanoid Avatar is missing or invalid: " + AvatarPath);
    }

    private static void RepairAnimationAvatars(Avatar avatar)
    {
        foreach (string file in Directory.GetFiles(AnimationRoot, "*.fbx", SearchOption.AllDirectories))
        {
            string path = file.Replace('\\', '/');
            ModelImporter importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null)
                throw new InvalidOperationException("Animation importer unavailable: " + path);
            if (importer.animationType == ModelImporterAnimationType.Human &&
                importer.avatarSetup == ModelImporterAvatarSetup.CopyFromOther && importer.sourceAvatar == avatar)
                continue;

            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
            importer.sourceAvatar = avatar;
            importer.SaveAndReimport();
        }
    }

    private static AnimatorController BuildController()
    {
        AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        if (controller == null)
        {
            controller = new AnimatorController { name = "RifleGirl" };
            AssetDatabase.CreateAsset(controller, ControllerPath);
        }
        else
        {
            controller.layers = Array.Empty<AnimatorControllerLayer>();
            controller.parameters = Array.Empty<AnimatorControllerParameter>();
            foreach (Object subAsset in AssetDatabase.LoadAllAssetsAtPath(ControllerPath))
            {
                if (subAsset != null && subAsset != controller)
                    Object.DestroyImmediate(subAsset, true);
            }
        }

        controller.AddParameter("MoveBlend", AnimatorControllerParameterType.Float);
        controller.AddParameter("AimingX", AnimatorControllerParameterType.Float);
        controller.AddParameter("AimingY", AnimatorControllerParameterType.Float);
        controller.AddParameter("Reload", AnimatorControllerParameterType.Trigger);
        controller.AddParameter("Equip", AnimatorControllerParameterType.Trigger);
        controller.AddParameter("Fire", AnimatorControllerParameterType.Trigger);
        controller.AddParameter(new AnimatorControllerParameter
        {
            name = "ReloadSpeed",
            type = AnimatorControllerParameterType.Float,
            defaultFloat = 1f,
        });
        controller.AddParameter(new AnimatorControllerParameter
        {
            name = "EquipSpeed",
            type = AnimatorControllerParameterType.Float,
            defaultFloat = 1f,
        });

        AnimationClip idle = CopyClip("AimIdle", AnimationRoot + "Aiming/R_AimIdle.fbx", true);
        AnimationClip walk = CopyClip("AimWalkForward", AnimationRoot + "Aiming/R_AimWalk_F.fbx", true);
        AnimationClip run = CopyClip("Run", AnimationRoot + "Normal/R_Run.fbx", true);
        AnimationClip hover = CopyClip("Hover", "Assets/Resource/Animations/Player/@Hover.fbx", true);
        AnimationClip reload = CopyClip("Reload", AnimationRoot + "Aiming/R_Reload.fbx", false);
        AnimationClip equip = CopyClip("Equip", AnimationRoot + "Normal/R_TakeGun.fbx", false);
        AnimationClip fire = CopyClip("Fire", AnimationRoot + "Aiming/R_Shoot.fbx", false);

        AnimatorStateMachine baseMachine = AddLayer(controller, "Base Layer", null, false);
        AnimatorState idleState = AddState(baseMachine, "Idle", idle, new Vector3(240, 50));
        baseMachine.defaultState = idleState;

        BlendTree moveTree = AddBlendTree(controller, "Movement", BlendTreeType.Simple1D, "MoveBlend");
        moveTree.useAutomaticThresholds = false;
        moveTree.AddChild(walk, 0f);
        moveTree.AddChild(run, 1f);
        AddState(baseMachine, "Move", moveTree, new Vector3(240, 120));
        AddState(baseMachine, "Hover", hover, new Vector3(240, 190));

        BlendTree aimTree = AddBlendTree(controller, "AimedMovement", BlendTreeType.FreeformDirectional2D, "AimingX");
        aimTree.blendParameterY = "AimingY";
        aimTree.AddChild(idle, Vector2.zero);
        aimTree.AddChild(walk, Vector2.up);
        AddAimMotion(aimTree, "AimWalkForwardLeft", "R_AimWalk_FL.fbx", new Vector2(-1, 1));
        AddAimMotion(aimTree, "AimWalkForwardRight", "R_AimWalk_FR.fbx", new Vector2(1, 1));
        AddAimMotion(aimTree, "AimWalkBackward", "R_AimWalk_B.fbx", Vector2.down);
        AddAimMotion(aimTree, "AimWalkBackwardLeft", "R_AimWalk_BL.fbx", new Vector2(-1, -1));
        AddAimMotion(aimTree, "AimWalkBackwardRight", "R_AimWalk_BR.fbx", new Vector2(1, -1));
        AddState(baseMachine, "Aiming", aimTree, new Vector3(240, 260));
        ChildMotion[] moveChildren = moveTree.children;
        moveChildren[0].motion = aimTree;
        moveTree.children = moveChildren;

        AvatarMask mask = BuildUpperBodyMask();
        AnimatorStateMachine upperMachine = AddLayer(controller, UpperBodyLayer, mask, false);
        AnimatorState empty = AddState(upperMachine, "Empty", null, new Vector3(220, 60));
        upperMachine.defaultState = empty;
        AnimatorState reloadState = AddActionState(upperMachine, empty, "Reload", reload, 150);
        reloadState.speedParameter = "ReloadSpeed";
        reloadState.speedParameterActive = true;
        AnimatorState equipState = AddActionState(upperMachine, empty, "Equip", equip, 230);
        equipState.speedParameter = "EquipSpeed";
        equipState.speedParameterActive = true;
        AddActionState(upperMachine, empty, "Fire", fire, 310);
        // 最后一层仅追加 IK。空 Override 状态会把 Humanoid 根位移覆盖为零，
        // 导致骨盆落到地面下；空 Additive 状态保留下层姿势与根高度。
        AnimatorStateMachine hands = AddLayer(controller, HandsIkLayer, null, true);
        hands.defaultState = AddState(hands, "IK", null, new Vector3(220, 60));
        AnimatorControllerLayer[] finalLayers = controller.layers;
        finalLayers[finalLayers.Length - 1].blendingMode = AnimatorLayerBlendingMode.Additive;
        controller.layers = finalLayers;
        EditorUtility.SetDirty(controller);
        return controller;
    }

    private static AnimatorStateMachine AddLayer(AnimatorController controller, string name, AvatarMask mask, bool ikPass)
    {
        AnimatorStateMachine machine = new AnimatorStateMachine { name = name };
        AssetDatabase.AddObjectToAsset(machine, controller);
        controller.AddLayer(new AnimatorControllerLayer
        {
            name = name,
            stateMachine = machine,
            // 运行时在 Reload/Equip/Fire 期间提升上身层权重，结束后淡出。
            defaultWeight = mask == null ? 1f : 0f,
            avatarMask = mask,
            blendingMode = AnimatorLayerBlendingMode.Override,
            iKPass = ikPass,
        });
        return machine;
    }

    private static AnimatorState AddState(AnimatorStateMachine machine, string name, Motion motion, Vector3 position)
    {
        AnimatorState state = machine.AddState(name, position);
        state.motion = motion;
        state.writeDefaultValues = false;
        return state;
    }

    private static AnimatorState AddActionState(
        AnimatorStateMachine machine, AnimatorState empty, string name, AnimationClip clip, float y)
    {
        AnimatorState state = AddState(machine, name, clip, new Vector3(460, y));
        AnimatorStateTransition enter = empty.AddTransition(state);
        enter.hasExitTime = false;
        enter.hasFixedDuration = true;
        enter.duration = 0.08f;
        enter.AddCondition(AnimatorConditionMode.If, 0, name);
        AnimatorStateTransition exit = state.AddTransition(empty);
        exit.hasExitTime = true;
        exit.exitTime = 0.95f;
        exit.hasFixedDuration = true;
        exit.duration = 0.08f;
        return state;
    }

    private static BlendTree AddBlendTree(
        AnimatorController controller, string name, BlendTreeType type, string parameter)
    {
        BlendTree tree = new BlendTree { name = name, blendType = type, blendParameter = parameter };
        AssetDatabase.AddObjectToAsset(tree, controller);
        return tree;
    }

    private static void AddAimMotion(BlendTree tree, string name, string file, Vector2 position)
    {
        tree.AddChild(CopyClip(name, AnimationRoot + "Aiming/" + file, true), position);
    }

    private static AnimationClip CopyClip(string name, string sourcePath, bool loop)
    {
        AnimationClip source = null;
        foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(sourcePath))
        {
            if (asset is AnimationClip clip && !clip.name.StartsWith("__preview__", StringComparison.Ordinal))
            {
                source = clip;
                break;
            }
        }
        if (source == null || !source.humanMotion)
            throw new InvalidOperationException("A valid Humanoid animation is required: " + sourcePath);

        AnimationClip copy = Object.Instantiate(source);
        copy.name = name;
        copy.hideFlags = HideFlags.None;
        copy.wrapMode = loop ? WrapMode.Loop : WrapMode.Once;
        AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(copy);
        settings.loopTime = loop;
        AnimationUtility.SetAnimationClipSettings(copy, settings);
        // 原 SwitchSocket 事件指向已移除的演示武器控制器；游戏逻辑不依赖这些事件。
        AnimationUtility.SetAnimationEvents(copy, Array.Empty<AnimationEvent>());

        string path = GeneratedAnimationRoot + "/" + name + ".anim";
        AnimationClip existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
        if (existing == null)
        {
            AssetDatabase.CreateAsset(copy, path);
            return copy;
        }
        EditorUtility.CopySerialized(copy, existing);
        Object.DestroyImmediate(copy);
        EditorUtility.SetDirty(existing);
        return existing;
    }

    private static AvatarMask BuildUpperBodyMask()
    {
        string path = GeneratedAnimationRoot + "/UpperBody.mask";
        AvatarMask mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(path);
        if (mask == null)
        {
            mask = new AvatarMask { name = "RifleGirl Upper Body" };
            AssetDatabase.CreateAsset(mask, path);
        }
        for (int i = 0; i < (int)AvatarMaskBodyPart.LastBodyPart; i++)
            mask.SetHumanoidBodyPartActive((AvatarMaskBodyPart)i, false);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Body, true);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Head, true);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftArm, true);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightArm, true);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftFingers, true);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightFingers, true);
        EditorUtility.SetDirty(mask);
        return mask;
    }

    private static void RemoveDemoComponentsAndWeapon(GameObject root)
    {
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            if (child == null)
                continue;
            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(child.gameObject);
            foreach (MonoBehaviour component in child.GetComponents<MonoBehaviour>())
            {
                string componentNamespace = component != null ? component.GetType().Namespace : null;
                if (componentNamespace != null && componentNamespace.StartsWith("CombatGirls", StringComparison.Ordinal))
                    Object.DestroyImmediate(component);
            }
        }

        foreach (ParentConstraint constraint in root.GetComponentsInChildren<ParentConstraint>(true))
            Object.DestroyImmediate(constraint);

        // 只移除源武器物体；hand_r、add_weapon_r 等骨骼/挂点仍留在同一骨架内。
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            if (child != null && child != root.transform && child.name == "Weapon_Rifle")
                Object.DestroyImmediate(child.gameObject);
        }
    }

    private static Mesh[] BuildFirstPersonHeadTemplates(GameObject root, Animator animator)
    {
        string folder = OutputRoot + "/FirstPersonHeads";
        EnsureFolder(folder);
        Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
        if (head == null) throw new InvalidOperationException("The first-person visibility mask requires the Humanoid Head bone.");
        var templates = new Mesh[BodyRendererPaths.Length];
        var entries = new List<HeadVisibilityEntry>();
        for (int index = 0; index < BodyRendererPaths.Length; index++)
        {
            var body = Require(root.transform, BodyRendererPaths[index]).GetComponent<SkinnedMeshRenderer>();
            Mesh source = body.sharedMesh;
            Transform[] bones = body.bones;
            BoneWeight[] weights = source.boneWeights;
            if (weights.Length != source.vertexCount) throw new InvalidOperationException("Body skin weights are missing: " + body.name);
            var headBones = new bool[bones.Length];
            for (int bone = 0; bone < bones.Length; bone++) headBones[bone] = bones[bone] != null && bones[bone].IsChildOf(head);
            var headWeights = new float[weights.Length];
            for (int vertex = 0; vertex < weights.Length; vertex++)
            {
                BoneWeight weight = weights[vertex];
                headWeights[vertex] = (headBones[weight.boneIndex0] ? weight.weight0 : 0f) +
                    (headBones[weight.boneIndex1] ? weight.weight1 : 0f) +
                    (headBones[weight.boneIndex2] ? weight.weight2 : 0f) +
                    (headBones[weight.boneIndex3] ? weight.weight3 : 0f);
            }
            string path = folder + "/" + body.name + "_FirstPerson.asset";
            var entry = new HeadVisibilityEntry { body = body.name, source = AssetDatabase.GetAssetPath(source),
                firstPersonTemplate = path, vertices = source.vertexCount };
            Mesh generated = Object.Instantiate(source);
            generated.name = body.name + "_FirstPerson";
            for (int submesh = 0; submesh < source.subMeshCount; submesh++)
            {
                int[] triangles = source.GetTriangles(submesh);
                var retained = new List<int>(triangles.Length);
                entry.sourceTriangles += triangles.Length / 3;
                for (int triangle = 0; triangle < triangles.Length; triangle += 3)
                {
                    int a = triangles[triangle], b = triangles[triangle + 1], c = triangles[triangle + 2];
                    // The body includes head surfaces in addition to the separate face.
                    // Hide triangles touching predominantly Head-driven vertices only
                    // for the local camera; all vertex positions/weights remain unchanged.
                    if (Mathf.Max(headWeights[a], Mathf.Max(headWeights[b], headWeights[c])) >= entry.headWeightThreshold)
                    { entry.hiddenTriangles++; continue; }
                    retained.Add(a); retained.Add(b); retained.Add(c);
                }
                generated.SetTriangles(retained, submesh, false);
            }
            if (entry.hiddenTriangles == 0 || entry.hiddenTriangles > entry.sourceTriangles / 6)
            { Object.DestroyImmediate(generated); throw new InvalidOperationException("Unexpected head visibility mask size: " + body.name); }
            Mesh template = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (template == null) { AssetDatabase.CreateAsset(generated, path); template = generated; }
            else { EditorUtility.CopySerialized(generated, template); Object.DestroyImmediate(generated); EditorUtility.SetDirty(template); }
            templates[index] = template;
            entries.Add(entry);
        }
        string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../tmp/PvpMigration"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "first-person-head-visibility.json"), JsonUtility.ToJson(new HeadVisibilityReport { bodies = entries.ToArray() }, true));
        return templates;
    }

    private static void ConfigureAppearance(Transform root, Animator animator, RifleGirlAppearance appearance, Mesh[] firstPersonTemplates)
    {
        GameObject[] outfits = { Require(root, "Jakcket"), Require(root, "Dress"), Require(root, "Sportswear") };
        GameObject[] faces =
        {
            Require(root, "Face/Face_01Re"), Require(root, "Face/Face_02"),
            Require(root, "Face/Face_03"), Require(root, "Face/Face_04"), Require(root, "Face/Face_05"),
        };
        GameObject hat = Require(root, "Rifle_Hat");
        Renderer hair = RequireRenderer(root, "Rifle_Hair");
        Renderer[] clothes =
        {
            RequireRenderer(root, "Jakcket/Rifle_Jacket"),
            RequireRenderer(root, "Jakcket/Rifle_Dress (1)"),
            RequireRenderer(root, "Jakcket/Rifle_Boots (1)"),
            RequireRenderer(root, "Dress/Rifle_Dress"),
            RequireRenderer(root, "Dress/Rifle_Boots"),
            RequireRenderer(root, "Rifle_Hat"),
        };
        Material[] clothPalette = new Material[RifleGirlAppearance.ClothColorCount];
        for (int i = 0; i < clothPalette.Length; i++)
            clothPalette[i] = RequireMaterial("Cloth/Rifle_Cloth " + (i + 1) + ".mat");
        Material[] hairPalette = new Material[RifleGirlAppearance.HairColorCount];
        for (int i = 0; i < hairPalette.Length; i++)
            hairPalette[i] = RequireMaterial("Hair/Rifle_Hair " + (i + 1) + ".mat");

        List<Renderer> hidden = new List<Renderer> { hair, hat.GetComponent<Renderer>() };
        foreach (GameObject face in faces)
            hidden.Add(face.GetComponent<Renderer>());

        var bodies = new SkinnedMeshRenderer[BodyRendererPaths.Length];
        for (int i = 0; i < bodies.Length; i++) bodies[i] = RequireRenderer(root, BodyRendererPaths[i]) as SkinnedMeshRenderer;
        appearance.ConfigureForEditor(animator, outfits, faces, hat, Require(root, "Acc"),
            clothes, hair, clothPalette, hairPalette, hidden.ToArray(), bodies, firstPersonTemplates);
    }

    private static GameObject Require(Transform root, string path)
    {
        Transform found = root.Find(path);
        if (found == null)
            throw new InvalidOperationException("RifleGirl part is missing: " + path);
        return found.gameObject;
    }

    private static Renderer RequireRenderer(Transform root, string path)
    {
        Renderer renderer = Require(root, path).GetComponent<Renderer>();
        if (renderer == null)
            throw new InvalidOperationException("RifleGirl renderer is missing: " + path);
        return renderer;
    }

    private static Material RequireMaterial(string relativePath)
    {
        string path = SourceRoot + "/RifleGirl/Materials/" + relativePath;
        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
            throw new InvalidOperationException("RifleGirl material is missing: " + path);
        return material;
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path))
            return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }
}
