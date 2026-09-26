using System;
using System.Collections.Generic;
using System.IO;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

/// <summary>Builds the PVP player from the shared wardrobe body without changing a loaded scene.</summary>
public static class PvpRifleGirlPrefabBuilder
{
    public const string PrefabPath = "Assets/Resources/Players/PVP/RifleGirl_PVP_Net.prefab";
    private const string NetworkListPath = "Assets/DefaultNetworkPrefabs.asset";
    private const string PreviousPlayerPath = "Assets/Resources/Players/PVP/Lumine_PVP_Net.prefab";
    private static readonly string OutputDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "../tmp/PvpMigration"));
    private static readonly HumanBodyBones[] RequiredBones =
    {
        HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Head,
        HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
        HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
        HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
        HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,
    };
    private static readonly Type[] NetworkOrder =
    {
        typeof(OwnerNetworkTransform), typeof(PlayerNetworkSync), typeof(PvpCharacterProfile),
        typeof(PvpFullBodyController), typeof(PvpLoadoutCombat), typeof(PvpPlayerCombat),
    };

    [Serializable] private sealed class ValidationResult
    {
        public string prefab;
        public string scope = "Editor prefab and resource validation; gameplay and multiplayer still require Play Mode validation.";
        public int animatorCount, skeletonBoneCount, skinnedRendererCount, networkPrefabCount;
        public float capsuleHeight, capsuleRadius, eyeHeight;
        public string[] networkBehaviourOrder;
    }

    [Serializable] private sealed class ReachReport
    {
        public string scope = "Neutral animated AimIdle pose, no recoil, pitch zero. Distances do not prove wrist orientation or clothing clearance.";
        public Vector3 hipPosition;
        public float aimEyeRelief, leftArmLength, rightArmLength;
        public ReachSample[] samples;
    }

    [Serializable] private sealed class ReachSample
    {
        public string weapon, stance;
        public int sightId;
        public float eyeRelief, leftDistance, rightDistance, leftRemainingReach, rightRemainingReach;
        public bool reachable;
    }

    [Serializable] private sealed class PoseMeasurements
    {
        public PoseMeasurement[] stages;
    }

    [Serializable] private sealed class PoseMeasurement
    {
        public string stage;
        public Vector3 root, hips, head, leftFoot, rightFoot, geometryCenter, geometrySize;
        public Vector3 leftShoulder, rightShoulder;
        public float humanScale;
    }

    [MenuItem("Tools/FPS/Build RifleGirl PVP Prefab")]
    public static void Build()
    {
        Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Leave Play Mode before building the player prefab.");
        Require(AssetDatabase.LoadAssetAtPath<GameObject>(RifleGirlAssetBuilder.BodyPrefabPath) != null,
            "Build RifleGirl body assets first.");
        EnsureFolder("Assets/Resources/Players/PVP");
        GameObject oldPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PreviousPlayerPath);
        Require(oldPrefab != null, "The existing Lumine PVP prefab is required for hit feedback settings.");
        NetworkPrefabsList list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(NetworkListPath);
        Require(list != null, "The project's DefaultNetworkPrefabs list is missing.");
        var preservedPrefabs = new List<GameObject>();
        foreach (NetworkPrefab entry in list.PrefabList) preservedPrefabs.Add(entry.Prefab);

        GameObject root = PrefabUtility.LoadPrefabContents(RifleGirlAssetBuilder.BodyPrefabPath);
        try
        {
            root.name = "RifleGirl_PVP_Net";
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            root.transform.localScale = Vector3.one;
            int playerLayer = LayerMask.NameToLayer("Player");
            Require(playerLayer >= 0, "The Player physics layer is missing.");
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = playerLayer;

            Animator animator = root.GetComponent<Animator>();
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            CharacterController capsule = root.AddComponent<CharacterController>();
            capsule.height = 1.72f;
            capsule.radius = 0.28f;
            capsule.center = new Vector3(0f, capsule.height * 0.5f, 0f);
            capsule.slopeLimit = 50f;
            capsule.stepOffset = 0.3f;
            capsule.skinWidth = 0.025f;
            capsule.minMoveDistance = 0f;

            // NGO invokes spawn callbacks in component order. The rig must be configured
            // before loadout callbacks and before combat caches the complete render hierarchy.
            root.AddComponent<NetworkObject>();
            OwnerNetworkTransform networkTransform = root.AddComponent<OwnerNetworkTransform>();
            networkTransform.Interpolate = true;
            networkTransform.SyncRotAngleX = false;
            networkTransform.SyncRotAngleY = true;
            networkTransform.SyncRotAngleZ = false;
            networkTransform.SyncScaleX = networkTransform.SyncScaleY = networkTransform.SyncScaleZ = false;
            networkTransform.PositionThreshold = 0.001f;
            networkTransform.RotAngleThreshold = 0.01f;

            PlayerModel model = root.AddComponent<PlayerModel>();
            model.animator = animator;
            model.cc = capsule;
            model.moveSpeed = 5f;
            model.sprintSpeed = 10f;
            model.gravity = -19.62f;
            model.useExternalMove = true;
            model.weapon = CreateCompatibilityWeapon(root, oldPrefab);
            root.AddComponent<PlayerNetworkSync>().playerModel = model;
            root.AddComponent<PvpCharacterProfile>();
            root.AddComponent<PvpWeaponRig>();
            PvpFullBodyController body = root.AddComponent<PvpFullBodyController>();
            body.eyeHeight = MeasureIdleEyeHeight();
            body.walkSpeed = 5f;
            body.sprintSpeed = 10f;
            body.movementSmoothTime = 0.13f;
            body.jumpSpeed = 6f;
            body.gravity = -19.62f;
            body.hipPosition = new Vector3(0.16f, -0.23f, 0.08f);
            body.aimEyeRelief = 0.12f;
            MeasureHandFrameCorrections(out Quaternion leftHandOffset, out Quaternion rightHandOffset);
            body.leftHandRotationOffset = leftHandOffset.eulerAngles;
            body.rightHandRotationOffset = rightHandOffset.eulerAngles;
            root.AddComponent<PvpLoadoutCombat>();
            CopyHitFeedback(oldPrefab.GetComponent<PvpPlayerCombat>(), root.AddComponent<PvpPlayerCombat>());
            root.GetComponent<RifleGirlAppearance>().Apply(CharacterAppearanceData.Default);
            ValidateStructure(root);
            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool savedSuccessfully);
            Require(savedSuccessfully && saved != null, "Saving the RifleGirl PVP prefab failed.");
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }

        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        Require(prefab != null, "Saving the RifleGirl PVP prefab failed.");
        if (!list.Contains(prefab)) list.Add(new NetworkPrefab { Prefab = prefab, Override = NetworkPrefabOverride.None });
        foreach (GameObject previous in preservedPrefabs)
            Require(previous == null || list.Contains(previous), "Building RifleGirl must preserve every existing network prefab.");
        EditorUtility.SetDirty(list);
        AssetDatabase.SaveAssets();
        RemoveObsoleteFootMasks();
        Debug.Log("[RifleGirl] Built and registered " + PrefabPath);
    }

    private static void RemoveObsoleteFootMasks()
    {
        // The apparent exposed feet came from stale editor GPU skinning. Restore
        // source geometry first, then remove only the three obsolete generated copies.
        var dependencies = new HashSet<string>(AssetDatabase.GetDependencies(
            new[] { RifleGirlAssetBuilder.BodyPrefabPath, PrefabPath }, true));
        foreach (string name in new[] { "Body_Jacke", "Body_Full", "Body_Sportswea" })
        {
            string path = RifleGirlAssetBuilder.OutputRoot + "/Meshes/" + name + "_ClosedBoots.asset";
            if (AssetDatabase.LoadAssetAtPath<Mesh>(path) == null) continue;
            Require(!dependencies.Contains(path), "Rebuild the source body before removing the obsolete foot mask: " + path);
            Require(AssetDatabase.DeleteAsset(path), "Could not delete obsolete generated mesh: " + path);
        }
    }

    private static PlayerWeapon CreateCompatibilityWeapon(GameObject root, GameObject oldPrefab)
    {
        var holder = new GameObject("LegacyWeaponData");
        holder.SetActive(false);
        holder.layer = root.layer;
        holder.transform.SetParent(root.transform, false);
        PlayerWeapon proxy = holder.AddComponent<PlayerWeapon>();
        PlayerWeapon source = oldPrefab.GetComponentInChildren<PlayerWeapon>(true);
        if (source != null) EditorUtility.CopySerialized(source, proxy);
        proxy.enabled = false;
        proxy.bulletSpawnPoint = holder.transform;
        // Runtime PvpLoadoutCombat replaces definition values and muzzle. This inactive
        // component only keeps existing damage/HUD consumers compatible; it never fires.
        return proxy;
    }

    private static float MeasureIdleEyeHeight()
    {
        // The aimed stance bends the knees. Use its actual head height instead of the
        // taller bind pose; measure a disposable body so saved skeleton transforms stay intact.
        GameObject sample = PrefabUtility.LoadPrefabContents(RifleGirlAssetBuilder.BodyPrefabPath);
        try
        {
            Animator animator = sample.GetComponent<Animator>();
            animator.Rebind();
            animator.Play("Base Layer.Idle", 0, 0f);
            animator.Update(1f / 60f);
            float height = sample.transform.InverseTransformPoint(animator.GetBoneTransform(HumanBodyBones.Head).position).y + 0.1f;
            Require(height > 1.2f && height < 1.7f, "Animated eye height is invalid; inspect the Humanoid root pose before building.");
            return height;
        }
        finally { PrefabUtility.UnloadPrefabContents(sample); }
    }

    private static void CopyHitFeedback(PvpPlayerCombat source, PvpPlayerCombat target)
    {
        Require(source != null, "The previous PVP prefab has no PvpPlayerCombat feedback settings.");
        target.maxHealth = source.maxHealth;
        target.respawnDelay = source.respawnDelay;
        target.headHitboxRadius = source.headHitboxRadius;
        target.headHitboxVerticalOffset = source.headHitboxVerticalOffset;
        target.bodyBloodImpactPrefab = source.bodyBloodImpactPrefab;
        target.headBloodImpactPrefab = source.headBloodImpactPrefab;
        target.bloodImpactLifetime = source.bloodImpactLifetime;
        target.headshotReceivedSound = source.headshotReceivedSound;
        target.headshotReceivedVolume = source.headshotReceivedVolume;
        GameObject demoBlood = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/PvpWeapons/Effects/BloodImpact.prefab");
        Require(demoBlood != null, "Build Demo weapon impact assets before the PVP player.");
        target.bodyBloodImpactPrefab = demoBlood;
        target.headBloodImpactPrefab = demoBlood;
    }

    [MenuItem("Tools/FPS/Validate RifleGirl PVP Prefab")]
    public static void Validate()
    {
        Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Prefab validation requires Edit Mode.");
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        Require(prefab != null, "Build the RifleGirl PVP prefab first.");
        var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(NetworkListPath);
        Require(list != null && list.Contains(prefab), "RifleGirl is not registered in DefaultNetworkPrefabs.");
        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            ValidationResult result = ValidateStructure(root);
            ValidateWardrobe(root);
            ValidateWeapons();
            WriteReachReport(root);
            result.prefab = PrefabPath;
            result.networkPrefabCount = list.PrefabList.Count;
            Directory.CreateDirectory(OutputDirectory);
            File.WriteAllText(Path.Combine(OutputDirectory, "prefab-validation.json"), JsonUtility.ToJson(result, true));
            Debug.Log("[RifleGirl] Prefab, wardrobe, shared skeleton, and weapon references passed editor validation.");
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }

    private static ValidationResult ValidateStructure(GameObject root)
    {
        Animator[] animators = root.GetComponentsInChildren<Animator>(true);
        Require(animators.Length == 1 && animators[0].gameObject == root, "Player must have exactly one root Animator.");
        Animator animator = animators[0];
        Require(animator.avatar != null && animator.avatar.isValid && animator.avatar.isHuman, "A valid Humanoid Avatar is required.");
        Require(!animator.applyRootMotion && animator.cullingMode == AnimatorCullingMode.AlwaysAnimate,
            "PVP motion is controller-driven; server bones must remain animated.");
        foreach (HumanBodyBones bone in RequiredBones)
            Require(animator.GetBoneTransform(bone) != null, "Missing Humanoid bone: " + bone);
        Require(root.GetComponentsInChildren<NetworkObject>(true).Length == 1, "Exactly one NetworkObject must own the entire body.");
        Require(root.GetComponent<NavMeshAgent>() == null, "PVP full body does not use a NavMeshAgent.");
        Require(root.GetComponentsInChildren<UnityEngine.Animations.ParentConstraint>(true).Length == 0,
            "Vendor weapon ParentConstraints must not compete with the hand IK.");
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            Require(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(child.gameObject) == 0, "Missing script: " + child.name);
            Require(child.name != "Weapon_Rifle", "Vendor demo gun must not be part of the PVP body.");
        }
        SkinnedMeshRenderer[] skins = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        var bones = new HashSet<Transform>();
        foreach (SkinnedMeshRenderer skin in skins)
        {
            Require(skin.sharedMesh != null, "Missing skinned mesh: " + skin.name);
            Require(skin.rootBone != null && skin.rootBone.IsChildOf(root.transform), "Mesh root bone is outside the player: " + skin.name);
            foreach (Transform bone in skin.bones)
            {
                Require(bone != null && bone.IsChildOf(root.transform), "Mesh has a missing or foreign bone: " + skin.name);
                bones.Add(bone);
            }
            foreach (Material material in skin.sharedMaterials)
                Require(material != null && material.shader != null, "Missing material or shader: " + skin.name);
        }
        var networkBehaviours = root.GetComponents<NetworkBehaviour>();
        Require(networkBehaviours.Length == NetworkOrder.Length, "Unexpected network component count.");
        var order = new string[networkBehaviours.Length];
        for (int i = 0; i < networkBehaviours.Length; i++)
        {
            order[i] = networkBehaviours[i].GetType().Name;
            Require(networkBehaviours[i].GetType() == NetworkOrder[i], "Network spawn order mismatch at " + order[i]);
        }
        PlayerModel model = root.GetComponent<PlayerModel>();
        CharacterController capsule = root.GetComponent<CharacterController>();
        PvpFullBodyController body = root.GetComponent<PvpFullBodyController>();
        Require(model != null && model.animator == animator && model.cc == capsule, "PlayerModel references are incomplete.");
        Require(capsule != null && capsule.height > capsule.radius * 2f,
            "Player capsule is invalid.");
        Require(body.eyeHeight > 1f && body.eyeHeight < capsule.height, "Eye position does not fit the body capsule.");
        Require(model.weapon != null && model.weapon.transform.IsChildOf(root.transform) && !model.weapon.gameObject.activeSelf,
            "An inactive legacy weapon data proxy is required.");
        Require(root.GetComponent<PlayerNetworkSync>().playerModel == model, "Network sync must reference this PlayerModel.");
        Require(root.GetComponent<PvpWeaponRig>() != null && root.GetComponent<RifleGirlAppearance>() != null,
            "Shared body presentation components are missing.");
        PvpPlayerCombat health = root.GetComponent<PvpPlayerCombat>();
        Require(health.bodyBloodImpactPrefab != null && health.headBloodImpactPrefab != null && health.headshotReceivedSound != null,
            "PVP hit feedback references are missing.");
        ValidateController(animator.runtimeAnimatorController as AnimatorController);
        return new ValidationResult
        {
            animatorCount = animators.Length, skeletonBoneCount = bones.Count, skinnedRendererCount = skins.Length,
            capsuleHeight = capsule.height, capsuleRadius = capsule.radius, eyeHeight = body.eyeHeight,
            networkBehaviourOrder = order,
        };
    }

    private static void ValidateController(AnimatorController controller)
    {
        Require(controller != null && controller.layers.Length == 3, "Expected locomotion, upper body, and final IK layers.");
        Require(controller.layers[1].name == "UpperBody" && controller.layers[2].name == "HandsIK" && controller.layers[2].iKPass,
            "The final HandsIK layer must execute after upper body actions.");
        Require(controller.layers[2].blendingMode == AnimatorLayerBlendingMode.Additive,
            "The empty final IK layer must be additive so it cannot zero the Humanoid root pose.");
        var parameters = new HashSet<string>();
        foreach (AnimatorControllerParameter parameter in controller.parameters) parameters.Add(parameter.name);
        foreach (string name in new[] { "MoveBlend", "AimingX", "AimingY", "ReloadSpeed", "EquipSpeed" })
            Require(parameters.Contains(name), "Animator parameter missing: " + name);
        foreach (string name in new[] { "Idle", "Move", "Hover", "Aiming" })
            Require(HasState(controller.layers[0].stateMachine, name), "Locomotion state missing: " + name);
        foreach (string name in new[] { "Empty", "Reload", "Equip" })
            Require(HasState(controller.layers[1].stateMachine, name), "Upper body state missing: " + name);
    }

    private static bool HasState(AnimatorStateMachine machine, string name)
    {
        foreach (ChildAnimatorState state in machine.states) if (state.state.name == name) return true;
        return false;
    }

    private static void ValidateWardrobe(GameObject root)
    {
        RifleGirlAppearance appearance = root.GetComponent<RifleGirlAppearance>();
        string[] outfitNames = { "Jakcket", "Dress", "Sportswear" };
        for (byte outfit = 0; outfit < RifleGirlAppearance.OutfitCount; outfit++)
        {
            for (byte face = 0; face < RifleGirlAppearance.FaceCount; face++)
            {
                CharacterAppearanceData data = CharacterAppearanceData.Default;
                data.outfitId = outfit; data.faceId = face;
                appearance.Apply(data);
                for (int i = 0; i < outfitNames.Length; i++)
                    Require(root.transform.Find(outfitNames[i]).gameObject.activeSelf == (i == outfit), "Outfits must be mutually exclusive.");
                Transform faces = root.transform.Find("Face");
                int activeFaces = 0;
                foreach (Transform child in faces) if (child.gameObject.activeSelf) activeFaces++;
                Require(activeFaces == 1, "Exactly one face must be visible.");
                Require(root.transform.Find("Acc").gameObject.activeSelf == (outfit != 2), "Sportswear cannot use combat outfit accessories.");
            }
        }
        appearance.Apply(CharacterAppearanceData.Default);
        var serialized = new SerializedObject(appearance);
        SerializedProperty hidden = serialized.FindProperty("firstPersonHiddenRenderers");
        Require(hidden.arraySize == RifleGirlAppearance.FaceCount + 2, "First-person visibility must cover every face, hair, and hat.");
        var original = new ShadowCastingMode[hidden.arraySize];
        for (int i = 0; i < hidden.arraySize; i++)
            original[i] = ((Renderer)hidden.GetArrayElementAtIndex(i).objectReferenceValue).shadowCastingMode;
        appearance.SetLocalFirstPerson(true);
        for (int i = 0; i < hidden.arraySize; i++)
            Require(((Renderer)hidden.GetArrayElementAtIndex(i).objectReferenceValue).shadowCastingMode == ShadowCastingMode.ShadowsOnly,
                "Local head geometry must be hidden without removing its bones or shadows.");
        Renderer body = root.transform.Find("Jakcket/Body_Jacke").GetComponent<Renderer>();
        Require(body.enabled && body.shadowCastingMode != ShadowCastingMode.ShadowsOnly, "First person must keep the shared body and arms.");
        appearance.SetLocalFirstPerson(false);
        for (int i = 0; i < hidden.arraySize; i++)
            Require(((Renderer)hidden.GetArrayElementAtIndex(i).objectReferenceValue).shadowCastingMode == original[i], "Third-person visibility did not restore.");
        var originalBodies = new Mesh[outfitNames.Length];
        string[] bodyNames = { "Body_Jacke", "Body_Full", "Body_Sportswea" };
        for (int outfit = 0; outfit < outfitNames.Length; outfit++)
        {
            var renderer = root.transform.Find(outfitNames[outfit] + "/" + bodyNames[outfit]).GetComponent<SkinnedMeshRenderer>();
            originalBodies[outfit] = renderer.sharedMesh;
        }
        appearance.SetLocalFirstPerson(true);
        for (byte outfit = 0; outfit < outfitNames.Length; outfit++)
        {
            CharacterAppearanceData data = CharacterAppearanceData.Default; data.outfitId = outfit;
            appearance.Apply(data);
            var renderer = root.transform.Find(outfitNames[outfit] + "/" + bodyNames[outfit]).GetComponent<SkinnedMeshRenderer>();
            Require(renderer.enabled && renderer.sharedMesh != originalBodies[outfit], "Local view must use a private head visibility mesh on the same body renderer.");
            Require(renderer.sharedMesh.vertexCount == originalBodies[outfit].vertexCount &&
                renderer.sharedMesh.bindposes.Length == originalBodies[outfit].bindposes.Length,
                "Local head visibility must preserve every vertex and skin binding.");
            Require(renderer.sharedMesh.triangles.Length < originalBodies[outfit].triangles.Length,
                "Local head visibility did not remove the body-integrated head faces.");
        }
        appearance.SetLocalFirstPerson(false);
        for (int outfit = 0; outfit < outfitNames.Length; outfit++)
            Require(root.transform.Find(outfitNames[outfit] + "/" + bodyNames[outfit]).GetComponent<SkinnedMeshRenderer>().sharedMesh == originalBodies[outfit],
                "Remote/third-person view must restore the complete source body mesh.");
        appearance.Apply(CharacterAppearanceData.Default);
        ValidatePaletteIsolation(root, appearance);
    }

    private static void ValidatePaletteIsolation(GameObject root, RifleGirlAppearance appearance)
    {
        GameObject other = PrefabUtility.LoadPrefabContents(RifleGirlAssetBuilder.BodyPrefabPath);
        try
        {
            Renderer otherHair = other.transform.Find("Rifle_Hair").GetComponent<Renderer>();
            Material initial = otherHair.sharedMaterial;
            var settings = new SerializedObject(appearance);
            SerializedProperty clothes = settings.FindProperty("clothMaterials");
            SerializedProperty hair = settings.FindProperty("hairMaterials");
            Require(clothes.arraySize == 5 && hair.arraySize == 4, "Wardrobe palettes are incomplete.");
            for (byte i = 0; i < 5; i++)
            {
                CharacterAppearanceData data = CharacterAppearanceData.Default;
                data.clothColorId = i; data.hairColorId = (byte)(i % 4);
                appearance.Apply(data);
                Require(root.transform.Find("Rifle_Hair").GetComponent<Renderer>().sharedMaterial == hair.GetArrayElementAtIndex(i % 4).objectReferenceValue,
                    "Hair palette did not apply.");
                Require(otherHair.sharedMaterial == initial, "Changing one player's palette changed another player's renderer.");
            }
        }
        finally { PrefabUtility.UnloadPrefabContents(other); }
        appearance.Apply(CharacterAppearanceData.Default);
    }

    private static void ValidateWeapons()
    {
        foreach (string name in new[] { "AssaultRifle", "Pistol", "Sniper" })
        {
            var definition = AssetDatabase.LoadAssetAtPath<PvpWeaponDefinition>("Assets/Resources/PvpWeapons/Definitions/" + name + ".asset");
            Require(definition != null && definition.modelPrefab != null, "Missing weapon definition/model: " + name);
            PvpWeaponModel model = definition.modelPrefab.GetComponent<PvpWeaponModel>();
            Require(model != null && model.muzzle != null && model.rightGrip != null && model.leftGrip != null && model.reloadHandTarget != null,
                "Weapon hand/muzzle contacts are incomplete: " + name);
            Require(definition.modelPrefab.GetComponentsInChildren<Animator>(true).Length == 0,
                "Mechanical weapon models must not contain a second character Animator: " + name);
            Require(model.sights != null && model.sights.Length > 0, "Weapon requires sight contacts: " + name);
            foreach (PvpSightModel sight in model.sights)
                Require(sight != null && sight.aimPoint != null, "Missing sight aim point: " + name);
        }
    }

    private static void WriteReachReport(GameObject root)
    {
        Animator animator = root.GetComponent<Animator>();
        AnimationClip idle = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Resource/Animations/RifleGirl/AimIdle.anim");
        Require(idle != null, "AimIdle clip is required for the reach check.");
        animator.Rebind();
        animator.Play("Base Layer.Idle", 0, 0.1f);
        animator.Update(0f);
        Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
        Vector3 withIkHips = hips.position, withIkHead = head.position;
        int ikLayer = animator.GetLayerIndex("HandsIK");
        animator.SetLayerWeight(ikLayer, 0f);
        animator.Play("Base Layer.Idle", 0, 0.1f);
        animator.Update(0f);
        Require(Vector3.Distance(withIkHips, hips.position) < 0.002f && Vector3.Distance(withIkHead, head.position) < 0.002f,
            "The empty HandsIK layer changed the underlying Humanoid pose.");
        animator.SetLayerWeight(ikLayer, 1f);
        animator.Play("Base Layer.Idle", 0, 0.1f);
        animator.Update(0f);
        Transform leftShoulder = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        Transform rightShoulder = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        float leftLength = ArmLength(animator, true), rightLength = ArmLength(animator, false);
        PvpFullBodyController body = root.GetComponent<PvpFullBodyController>();
        Vector3 eye = root.transform.position + Vector3.up * body.eyeHeight;
        var samples = new List<ReachSample>();
        foreach (string name in new[] { "AssaultRifle", "Pistol", "Sniper" })
        {
            var definition = AssetDatabase.LoadAssetAtPath<PvpWeaponDefinition>("Assets/Resources/PvpWeapons/Definitions/" + name + ".asset");
            PvpWeaponModel model = definition.modelPrefab.GetComponent<PvpWeaponModel>();
            Quaternion neutralRotation = Quaternion.Euler(model.neutralEuler);
            Vector3 right = neutralRotation * model.transform.InverseTransformPoint(model.rightGrip.position);
            Vector3 left = neutralRotation * model.transform.InverseTransformPoint(model.leftGrip.position);
            Vector3 hip = eye + body.hipPosition + model.neutralPosition;
            samples.Add(MeasureReach(name, "Hip", -1, hip + left, hip + right, leftShoulder.position, rightShoulder.position, leftLength, rightLength));
            foreach (PvpSightModel sight in model.sights)
            {
                Vector3 sightPoint = neutralRotation * model.transform.InverseTransformPoint(sight.aimPoint.position);
                float eyeRelief = Mathf.Max(body.aimEyeRelief, sight.eyeRelief);
                Vector3 ads = eye + Vector3.forward * eyeRelief - sightPoint;
                ReachSample sample = MeasureReach(name, "ADS", sight.sightId, ads + left, ads + right,
                    leftShoulder.position, rightShoulder.position, leftLength, rightLength);
                sample.eyeRelief = eyeRelief;
                samples.Add(sample);
            }
        }
        var report = new ReachReport { hipPosition = body.hipPosition, aimEyeRelief = body.aimEyeRelief,
            leftArmLength = leftLength, rightArmLength = rightLength, samples = samples.ToArray() };
        Directory.CreateDirectory(OutputDirectory);
        File.WriteAllText(Path.Combine(OutputDirectory, "hand-reach-validation.json"), JsonUtility.ToJson(report, true));
        foreach (ReachSample sample in samples)
            if (!sample.reachable) Debug.LogWarning("[RifleGirl] Neutral hand reach exceeds the arm: " + sample.weapon + " " + sample.stance +
                " sight=" + sample.sightId + " left margin=" + sample.leftRemainingReach.ToString("F3") +
                "m right margin=" + sample.rightRemainingReach.ToString("F3") + "m. See hand-reach-validation.json.");
    }

    private static float ArmLength(Animator animator, bool left)
    {
        Transform upper = animator.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
        Transform lower = animator.GetBoneTransform(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
        Transform hand = animator.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
        return Vector3.Distance(upper.position, lower.position) + Vector3.Distance(lower.position, hand.position);
    }

    private static ReachSample MeasureReach(string weapon, string stance, int sight, Vector3 leftTarget, Vector3 rightTarget,
        Vector3 leftShoulder, Vector3 rightShoulder, float leftLength, float rightLength)
    {
        float leftDistance = Vector3.Distance(leftShoulder, leftTarget), rightDistance = Vector3.Distance(rightShoulder, rightTarget);
        return new ReachSample { weapon = weapon, stance = stance, sightId = sight,
            leftDistance = leftDistance, rightDistance = rightDistance,
            leftRemainingReach = leftLength - leftDistance, rightRemainingReach = rightLength - rightDistance,
            reachable = leftDistance <= leftLength && rightDistance <= rightLength };
    }

    [MenuItem("Tools/FPS/Capture RifleGirl Wardrobe Preview")]
    public static void CapturePreview()
    {
        Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Wardrobe preview capture requires Edit Mode.");
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RifleGirlAssetBuilder.BodyPrefabPath);
        Require(prefab != null, "Build the RifleGirl body assets first.");
        Directory.CreateDirectory(OutputDirectory);
        var preview = new PreviewRenderUtility();
        try
        {
            GameObject root = preview.InstantiatePrefabInScene(prefab);
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(0f, 180f, 0f));
            Animator animator = root.GetComponent<Animator>();
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var poseMeasurements = new List<PoseMeasurement> { MeasurePose(root, "BeforeRebind") };
            animator.Rebind();
            poseMeasurements.Add(MeasurePose(root, "AfterRebind"));
            animator.Update(0f);
            poseMeasurements.Add(MeasurePose(root, "AfterAnimatorUpdateZero"));
            animator.Update(1f / 60f);
            poseMeasurements.Add(MeasurePose(root, "AfterAnimatorUpdateFrame"));
            int handsLayer = animator.GetLayerIndex("HandsIK");
            if (handsLayer >= 0)
            {
                animator.SetLayerWeight(handsLayer, 0f);
                animator.Update(1f / 60f);
                poseMeasurements.Add(MeasurePose(root, "HandsIKWeightZero"));
                animator.SetLayerWeight(handsLayer, 1f);
                animator.Update(1f / 60f);
                poseMeasurements.Add(MeasurePose(root, "HandsIKWeightOneRestored"));
            }
            File.WriteAllText(Path.Combine(OutputDirectory, "preview-pose-measurements.json"),
                JsonUtility.ToJson(new PoseMeasurements { stages = poseMeasurements.ToArray() }, true));
            preview.camera.fieldOfView = 34f;
            preview.camera.nearClipPlane = 0.03f;
            preview.camera.farClipPlane = 20f;
            preview.camera.clearFlags = CameraClearFlags.SolidColor;
            preview.camera.backgroundColor = new Color(0.065f, 0.075f, 0.1f, 1f);
            preview.lights[0].intensity = 1.4f;
            preview.lights[0].transform.rotation = Quaternion.Euler(40f, 150f, 0f);
            preview.lights[1].intensity = 0.65f;
            preview.lights[1].transform.rotation = Quaternion.Euler(340f, 330f, 0f);
            preview.ambientColor = new Color(0.4f, 0.4f, 0.4f, 1f);
            for (byte i = 0; i < RifleGirlAppearance.OutfitCount; i++)
            {
                CharacterAppearanceData appearance = CharacterAppearanceData.Default;
                appearance.outfitId = i;
                root.GetComponent<RifleGirlAppearance>().Apply(appearance);
                FrameGeometry(preview.camera, GeometryBounds(root));
                RenderFrozenSkins(preview, root, Path.Combine(OutputDirectory, "riflegirl-outfit-" + i + ".png"));
            }
        }
        finally { preview.Cleanup(); }
        Debug.Log("[RifleGirl] Captured three real editor wardrobe renders in " + OutputDirectory);
    }

    public static void BuildLobbyPreview()
    {
        RifleGirlAssetBuilder.BuildLobbyPreview();
    }

    [MenuItem("Tools/FPS/Capture RifleGirl Lobby Preview")]
    public static void CaptureLobbyPreview()
    {
        Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Lobby preview capture requires Edit Mode.");
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RifleGirlAssetBuilder.BodyPrefabPath);
        var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(RifleGirlAssetBuilder.LobbyControllerPath);
        AnimationClip idle = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Resource/Animations/RifleGirl/LobbyPreviewIdle.anim");
        Require(prefab != null && controller != null && idle != null, "Build RifleGirl lobby preview assets first.");
        Require(idle.events.Length == 0, "Lobby clip must not invoke demo weapon events.");
        Directory.CreateDirectory(OutputDirectory);
        var preview = new PreviewRenderUtility();
        try
        {
            GameObject root = preview.InstantiatePrefabInScene(prefab);
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(0f, 180f, 0f));
            RifleGirlAppearance appearance = root.GetComponent<RifleGirlAppearance>();
            Animator animator = root.GetComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.Rebind();
            preview.camera.fieldOfView = 34f;
            preview.camera.nearClipPlane = 0.03f;
            preview.camera.farClipPlane = 20f;
            preview.camera.clearFlags = CameraClearFlags.SolidColor;
            preview.camera.backgroundColor = new Color(0.065f, 0.075f, 0.1f, 1f);
            preview.lights[0].intensity = 1.4f;
            preview.lights[0].transform.rotation = Quaternion.Euler(40f, 150f, 0f);
            preview.lights[1].intensity = 0.65f;
            preview.lights[1].transform.rotation = Quaternion.Euler(340f, 330f, 0f);
            preview.ambientColor = new Color(0.4f, 0.4f, 0.4f, 1f);
            var measurements = new List<PoseMeasurement>();
            for (byte outfit = 0; outfit < RifleGirlAppearance.OutfitCount; outfit++)
            {
                CharacterAppearanceData data = CharacterAppearanceData.Default;
                data.outfitId = outfit;
                appearance.Apply(data);
                for (int time = 0; time < 3; time++)
                {
                    animator.Play("Base Layer.Idle", 0, time / Mathf.Max(0.01f, idle.length));
                    animator.Update(0f);
                    FrameGeometry(preview.camera, GeometryBounds(root));
                    RenderFrozenSkins(preview, root, Path.Combine(OutputDirectory, "lobby-outfit-" + outfit + "-time-" + time + ".png"));
                    if (outfit == 0) measurements.Add(MeasurePose(root, "LobbyIdleTime" + time));
                }
            }
            animator.Play("Base Layer.Idle", 0, 1f / Mathf.Max(0.01f, idle.length));
            animator.Update(0f);
            for (byte face = 0; face < RifleGirlAppearance.FaceCount; face++)
            {
                CharacterAppearanceData data = CharacterAppearanceData.Default;
                data.faceId = face;
                appearance.Apply(data);
                Vector3 center = animator.GetBoneTransform(HumanBodyBones.Head).position + Vector3.up * 0.08f;
                preview.camera.fieldOfView = 25f;
                preview.camera.transform.position = center + Vector3.back * 0.85f;
                preview.camera.transform.LookAt(center);
                RenderFrozenSkins(preview, root, Path.Combine(OutputDirectory, "lobby-face-" + face + ".png"));
            }
            File.WriteAllText(Path.Combine(OutputDirectory, "lobby-pose-measurements.json"),
                JsonUtility.ToJson(new PoseMeasurements { stages = measurements.ToArray() }, true));
        }
        finally { preview.Cleanup(); }
        Debug.Log("[RifleGirl] Captured lobby idle at three times for each outfit and all five face presets.");
    }

    [MenuItem("Tools/FPS/Capture RifleGirl Foot Coverage")]
    public static void CaptureFootPreview()
    {
        Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Foot coverage capture requires Edit Mode.");
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RifleGirlAssetBuilder.BodyPrefabPath);
        var gameplay = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(RifleGirlAssetBuilder.ControllerPath);
        var lobby = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(RifleGirlAssetBuilder.LobbyControllerPath);
        Require(prefab != null && gameplay != null && lobby != null, "Build the RifleGirl assets before checking foot coverage.");
        Directory.CreateDirectory(OutputDirectory);
        var preview = new PreviewRenderUtility();
        try
        {
            GameObject root = preview.InstantiatePrefabInScene(prefab);
            root.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            RifleGirlAppearance appearance = root.GetComponent<RifleGirlAppearance>();
            Animator animator = root.GetComponent<Animator>();
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            preview.camera.fieldOfView = 34f;
            preview.camera.nearClipPlane = 0.03f;
            preview.camera.farClipPlane = 20f;
            preview.camera.clearFlags = CameraClearFlags.SolidColor;
            preview.camera.backgroundColor = new Color(0.065f, 0.075f, 0.1f, 1f);
            preview.lights[0].intensity = 1.4f;
            preview.lights[0].transform.rotation = Quaternion.Euler(40f, 150f, 0f);
            preview.lights[1].intensity = 0.65f;
            preview.lights[1].transform.rotation = Quaternion.Euler(340f, 330f, 0f);
            preview.ambientColor = new Color(0.4f, 0.4f, 0.4f, 1f);
            string[] poses = { "LobbyIdle", "Walk", "AimIdle" };
            for (byte outfit = 0; outfit < RifleGirlAppearance.OutfitCount; outfit++)
            {
                CharacterAppearanceData data = CharacterAppearanceData.Default;
                data.outfitId = outfit;
                appearance.Apply(data);
                for (int pose = 0; pose < poses.Length; pose++)
                {
                    animator.runtimeAnimatorController = pose == 0 ? lobby : gameplay;
                    animator.Rebind();
                    if (pose != 0) { animator.SetFloat("AimingY", 1f); animator.SetFloat("MoveBlend", 0f); }
                    for (int frame = 0; frame < 2; frame++)
                    {
                        animator.Play("Base Layer." + (pose == 1 ? "Move" : "Idle"), 0, frame == 0 ? 0.15f : 0.65f);
                        animator.Update(0f);
                        Bounds legs = GeometryBounds(root, 0.85f);
                        for (int view = 0; view < 2; view++)
                        {
                            FrameGeometry(preview.camera, legs);
                            if (view == 1)
                            {
                                float distance = Vector3.Distance(preview.camera.transform.position, legs.center);
                                preview.camera.transform.position = legs.center + new Vector3(1f, 0f, -1f).normalized * distance;
                                preview.camera.transform.LookAt(legs.center);
                            }
                            RenderFrozenSkins(preview, root, Path.Combine(OutputDirectory,
                                "feet-" + outfit + "-" + poses[pose] + "-" + frame + "-" + view + ".png"));
                        }
                    }
                }
            }
        }
        finally { preview.Cleanup(); }
        Debug.Log("[RifleGirl] Captured front/oblique foot coverage for three outfits, three poses, and two animation times.");
    }

    public static void CaptureSkinningComparison()
    {
        Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Skinning comparison requires Edit Mode.");
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RifleGirlAssetBuilder.BodyPrefabPath);
        GameObject imported = AssetDatabase.LoadAssetAtPath<GameObject>(RifleGirlAssetBuilder.SourceRoot + "/RifleGirl/Models/Rifle_Full_Body.FBX");
        var gameplay = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(RifleGirlAssetBuilder.ControllerPath);
        var lobby = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(RifleGirlAssetBuilder.LobbyControllerPath);
        Require(prefab != null && imported != null && gameplay != null && lobby != null, "Skinning comparison assets are missing.");
        Directory.CreateDirectory(OutputDirectory);
        var preview = new PreviewRenderUtility();
        try
        {
            GameObject root = preview.InstantiatePrefabInScene(prefab);
            root.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            Animator animator = root.GetComponent<Animator>();
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            preview.camera.fieldOfView = 34f;
            preview.camera.nearClipPlane = 0.03f;
            preview.camera.farClipPlane = 20f;
            preview.camera.clearFlags = CameraClearFlags.SolidColor;
            preview.camera.backgroundColor = new Color(0.065f, 0.075f, 0.1f, 1f);
            preview.lights[0].intensity = 1.4f;
            preview.lights[0].transform.rotation = Quaternion.Euler(40f, 150f, 0f);
            preview.lights[1].intensity = 0.65f;
            preview.lights[1].transform.rotation = Quaternion.Euler(340f, 330f, 0f);
            preview.ambientColor = new Color(0.4f, 0.4f, 0.4f, 1f);
            string[] paths = { "Jakcket/Body_Jacke", "Dress/Body_Full", "Sportswear/Body_Sportswea" };
            string[] poses = { "LobbyIdle", "Walk", "AimIdle" };
            SkinnedMeshRenderer[] canonical = imported.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (byte outfit = 0; outfit < 3; outfit++)
            {
                CharacterAppearanceData appearance = CharacterAppearanceData.Default;
                appearance.outfitId = outfit;
                root.GetComponent<RifleGirlAppearance>().Apply(appearance);
                var body = root.transform.Find(paths[outfit]).GetComponent<SkinnedMeshRenderer>();
                Mesh generated = body.sharedMesh, original = null;
                foreach (SkinnedMeshRenderer candidate in canonical) if (candidate.name == body.name) { original = candidate.sharedMesh; break; }
                Require(original != null, "Cannot locate original body mesh for comparison.");
                for (int pose = 0; pose < poses.Length; pose++)
                {
                    body.sharedMesh = original;
                    animator.runtimeAnimatorController = pose == 0 ? lobby : gameplay;
                    animator.Rebind();
                    if (pose != 0) { animator.SetFloat("AimingY", 1f); animator.SetFloat("MoveBlend", 0f); }
                    animator.Play("Base Layer." + (pose == 1 ? "Move" : "Idle"), 0, pose == 0 ? 0.33f : 0.15f);
                    animator.Update(0f);
                    Bounds legs = GeometryBounds(root, 0.85f);
                    FrameGeometry(preview.camera, legs);
                    float distance = Vector3.Distance(preview.camera.transform.position, legs.center);
                    preview.camera.transform.position = legs.center + new Vector3(1f, 0f, -1f).normalized * distance;
                    preview.camera.transform.LookAt(legs.center);
                    string prefix = Path.Combine(OutputDirectory, "skinning-" + outfit + "-" + poses[pose]);
                    RenderPreviewImage(preview, prefix + "-source-live.png");
                    RenderFrozenSkins(preview, root, prefix + "-source-cpu.png");
                    body.sharedMesh = generated;
                    RenderFrozenSkins(preview, root, prefix + "-generated-cpu.png");
                }
                body.sharedMesh = generated;
            }
        }
        finally { preview.Cleanup(); }
        Debug.Log("[RifleGirl] Compared original live skinning, original CPU baked pose, and generated CPU baked pose; no assets modified.");
    }

    [Serializable] private sealed class HandInspection
    {
        public HandJoint[] referenceJoints, characterJoints;
        public HandMuscle[] muscles;
        public Vector3 leftBaseIkEuler, rightBaseIkEuler, leftBaseBoneEuler, rightBaseBoneEuler;
        public Vector3 leftResultBoneEuler, rightResultBoneEuler, leftGripEuler, rightGripEuler;
        public Vector3 leftCorrectionEuler, rightCorrectionEuler;
        public int ikCallbacks;
        public string[] visibleRenderers;
    }
    [Serializable] private sealed class HandJoint
    {
        public string name, parent;
        public Vector3 position, handLocalPosition, localEuler, handLocalEuler;
    }
    [Serializable] private sealed class HandMuscle { public string name; public float value; }

    public static void InspectHands()
    {
        Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Hand inspection requires Edit Mode.");
        Directory.CreateDirectory(OutputDirectory);
        var preview = new PreviewRenderUtility();
        try
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RifleGirlAssetBuilder.BodyPrefabPath);
            GameObject root = preview.InstantiatePrefabInScene(prefab);
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            Animator animator = root.GetComponent<Animator>();
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var appearance = root.GetComponent<RifleGirlAppearance>();
            CharacterAppearanceData data = CharacterAppearanceData.Default;
            data.faceId = 1; data.clothColorId = 2; data.hairColorId = 1;
            appearance.Apply(data);
            var driver = root.AddComponent<RifleGirlHandPoseDiagnostic>();
            driver.animator = animator;
            driver.eye = new Vector3(0f, 1.4553922f, 0f);
            GameObject weapon = preview.InstantiatePrefabInScene(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/PvpWeapons/Models/AssaultRifle.prefab"));
            PvpWeaponModel model = weapon.GetComponent<PvpWeaponModel>();
            weapon.transform.SetPositionAndRotation(driver.eye + new Vector3(.16f, -.23f, .08f) + model.neutralPosition, Quaternion.Euler(model.neutralEuler));
            foreach (PvpSightModel sight in model.sights) if (sight.visual != null) sight.visual.SetActive(sight.sightId == 1);
            driver.leftGrip = model.leftGrip; driver.rightGrip = model.rightGrip;
            animator.Rebind();
            animator.Play("Base Layer.Idle", 0, .15f);
            animator.Update(0f);
            var report = new HandInspection();
            var characterJoints = new List<HandJoint>();
            RecordHand(animator.GetBoneTransform(HumanBodyBones.LeftHand), characterJoints);
            RecordHand(animator.GetBoneTransform(HumanBodyBones.RightHand), characterJoints);
            report.characterJoints = characterJoints.ToArray();
            using (var handler = new HumanPoseHandler(animator.avatar, root.transform))
            {
                HumanPose pose = default; handler.GetHumanPose(ref pose);
                var muscles = new List<HandMuscle>();
                for (int i = 0; i < HumanTrait.MuscleCount; i++)
                {
                    string name = HumanTrait.MuscleName[i];
                    if (name.Contains("Thumb") || name.Contains("Index") || name.Contains("Middle") || name.Contains("Ring") || name.Contains("Little"))
                        muscles.Add(new HandMuscle { name = name, value = pose.muscles[i] });
                }
                report.muscles = muscles.ToArray();
            }
            string sourceRoot = "Assets/PvpWeapons/Source/";
            GameObject reference = preview.InstantiatePrefabInScene(AssetDatabase.LoadAssetAtPath<GameObject>(sourceRoot + "Prefabs/Weapons/Assault Rifle_1.prefab"));
            Animator sourceAnimator = reference.GetComponentInChildren<Animator>(true);
            sourceAnimator.enabled = false;
            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(sourceRoot + "Art/Models/Weapons/Assault Rifle_1/Assault Rifle_1 Animated.fbx"))
                if (asset is AnimationClip clip && (clip.name == "Idle" || clip.name.EndsWith("|Idle", StringComparison.Ordinal)))
                { clip.SampleAnimation(sourceAnimator.gameObject, 0f); break; }
            var referenceJoints = new List<HandJoint>();
            Transform sourceLeft = null, sourceRight = null;
            foreach (Transform bone in sourceAnimator.GetComponentsInChildren<Transform>(true))
            {
                if (bone.name == "hand_L.L") { sourceLeft = bone; RecordHand(bone, referenceJoints); }
                if (bone.name == "hand_L.R") { sourceRight = bone; RecordHand(bone, referenceJoints); }
            }
            report.referenceJoints = referenceJoints.ToArray();
            reference.SetActive(false);
            driver.applyGoals = true;
            animator.Update(0f);
            report.ikCallbacks = driver.callbacks;
            report.leftBaseIkEuler = driver.leftBaseIk.eulerAngles; report.rightBaseIkEuler = driver.rightBaseIk.eulerAngles;
            report.leftBaseBoneEuler = driver.leftBaseBone.eulerAngles; report.rightBaseBoneEuler = driver.rightBaseBone.eulerAngles;
            report.leftResultBoneEuler = animator.GetBoneTransform(HumanBodyBones.LeftHand).rotation.eulerAngles;
            report.rightResultBoneEuler = animator.GetBoneTransform(HumanBodyBones.RightHand).rotation.eulerAngles;
            report.leftGripEuler = model.leftGrip.rotation.eulerAngles; report.rightGripEuler = model.rightGrip.rotation.eulerAngles;
            Transform characterLeft = animator.GetBoneTransform(HumanBodyBones.LeftHand);
            Transform characterRight = animator.GetBoneTransform(HumanBodyBones.RightHand);
            Quaternion leftIkToBone = Quaternion.Inverse(model.leftGrip.rotation) * characterLeft.rotation;
            Quaternion rightIkToBone = Quaternion.Inverse(model.rightGrip.rotation) * characterRight.rotation;
            Quaternion leftCorrection = PalmFrame(sourceLeft, "index_L.L", "middle_L.L", "pink_L.L") *
                Quaternion.Inverse(PalmFrame(characterLeft, "index_01_l", "middle_01_l", "pinky_01_l")) * Quaternion.Inverse(leftIkToBone);
            Quaternion rightCorrection = PalmFrame(sourceRight, "index_L.R", "middle_L.R", "pink_L.R") *
                Quaternion.Inverse(PalmFrame(characterRight, "index_01_r", "middle_01_r", "pinky_01_r")) * Quaternion.Inverse(rightIkToBone);
            report.leftCorrectionEuler = leftCorrection.eulerAngles; report.rightCorrectionEuler = rightCorrection.eulerAngles;
            preview.camera.fieldOfView = 60f;
            preview.camera.nearClipPlane = .035f; preview.camera.farClipPlane = 20f;
            preview.camera.clearFlags = CameraClearFlags.SolidColor;
            preview.camera.backgroundColor = new Color(.2f, .3f, .4f, 1f);
            preview.lights[0].intensity = 1.4f;
            preview.lights[0].transform.rotation = Quaternion.Euler(40f, 150f, 0f);
            preview.lights[1].intensity = .65f;
            preview.ambientColor = new Color(.4f, .4f, .4f, 1f);
            appearance.SetLocalFirstPerson(true);
            preview.camera.transform.SetPositionAndRotation(driver.eye, Quaternion.identity);
            RenderFrozenSkins(preview, root, Path.Combine(OutputDirectory, "hands-current-firstperson.png"));
            var visible = new List<string>();
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled) continue;
                visible.Add(renderer.name);
                renderer.enabled = false;
                RenderFrozenSkins(preview, root, Path.Combine(OutputDirectory, "hands-without-" + renderer.name.Replace(' ', '_') + ".png"));
                renderer.enabled = true;
            }
            report.visibleRenderers = visible.ToArray();
            appearance.SetLocalFirstPerson(false);
            preview.camera.fieldOfView = 40f;
            Vector3 center = (model.leftGrip.position + model.rightGrip.position) * .5f;
            preview.camera.transform.position = center + new Vector3(-.6f, .18f, .7f);
            preview.camera.transform.LookAt(center);
            RenderFrozenSkins(preview, root, Path.Combine(OutputDirectory, "hands-current-observer.png"));
            driver.leftRotationOffset = leftCorrection; driver.rightRotationOffset = rightCorrection;
            animator.Update(0f);
            RenderFrozenSkins(preview, root, Path.Combine(OutputDirectory, "hands-corrected-observer.png"));
            appearance.SetLocalFirstPerson(true);
            preview.camera.fieldOfView = 60f;
            preview.camera.transform.SetPositionAndRotation(driver.eye, Quaternion.identity);
            RenderFrozenSkins(preview, root, Path.Combine(OutputDirectory, "hands-corrected-firstperson.png"));
            for (byte outfit = 0; outfit < RifleGirlAppearance.OutfitCount; outfit++)
            {
                data.outfitId = outfit;
                appearance.Apply(data);
                RenderFrozenSkins(preview, root, Path.Combine(OutputDirectory, "hands-corrected-outfit-" + outfit + ".png"));
            }
            File.WriteAllText(Path.Combine(OutputDirectory, "hand-inspection.json"), JsonUtility.ToJson(report, true));
        }
        finally { preview.Cleanup(); }
        Debug.Log("[RifleGirl] Inspected hand frames and isolated first-person renderers without modifying assets.");
    }

    private static void RecordHand(Transform hand, List<HandJoint> joints)
    {
        foreach (Transform bone in hand.GetComponentsInChildren<Transform>(true))
            joints.Add(new HandJoint { name = bone.name, parent = bone.parent != null ? bone.parent.name : "",
                position = bone.position, localEuler = bone.localEulerAngles,
                handLocalPosition = hand.InverseTransformPoint(bone.position),
                handLocalEuler = (Quaternion.Inverse(hand.rotation) * bone.rotation).eulerAngles });
    }

    private static Quaternion PalmFrame(Transform hand, string index, string middle, string little)
    {
        Require(hand != null, "Reference hand is missing.");
        Vector3 longitudinal = hand.InverseTransformPoint(hand.Find(middle).position);
        Vector3 transverse = hand.InverseTransformVector(hand.Find(index).position - hand.Find(little).position);
        return Quaternion.LookRotation(longitudinal.normalized, transverse.normalized);
    }

    private static void MeasureHandFrameCorrections(out Quaternion left, out Quaternion right)
    {
        // Generic hand_L bones and Humanoid IK goals use different axes. Recompute
        // the anatomical palm mapping and Avatar wrist conversion from real poses
        // whenever the prefab is built; no model dimensions or bone scales change.
        var preview = new PreviewRenderUtility();
        try
        {
            GameObject root = preview.InstantiatePrefabInScene(AssetDatabase.LoadAssetAtPath<GameObject>(RifleGirlAssetBuilder.BodyPrefabPath));
            Animator animator = root.GetComponent<Animator>();
            var driver = root.AddComponent<RifleGirlHandPoseDiagnostic>();
            driver.animator = animator; driver.eye = Vector3.up * 1.4553922f;
            GameObject weapon = preview.InstantiatePrefabInScene(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/PvpWeapons/Models/AssaultRifle.prefab"));
            PvpWeaponModel model = weapon.GetComponent<PvpWeaponModel>();
            weapon.transform.SetPositionAndRotation(driver.eye + new Vector3(.16f, -.23f, .08f) + model.neutralPosition, Quaternion.Euler(model.neutralEuler));
            driver.leftGrip = model.leftGrip; driver.rightGrip = model.rightGrip; driver.applyGoals = true;
            animator.Rebind(); animator.Play("Base Layer.Idle", 0, .15f); animator.Update(0f);
            Require(driver.callbacks > 0, "Humanoid IK calibration callback did not run.");
            Transform characterLeft = animator.GetBoneTransform(HumanBodyBones.LeftHand);
            Transform characterRight = animator.GetBoneTransform(HumanBodyBones.RightHand);
            Quaternion leftIkToBone = Quaternion.Inverse(model.leftGrip.rotation) * characterLeft.rotation;
            Quaternion rightIkToBone = Quaternion.Inverse(model.rightGrip.rotation) * characterRight.rotation;
            string sourcePath = "Assets/PvpWeapons/Source/";
            GameObject reference = preview.InstantiatePrefabInScene(AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath + "Prefabs/Weapons/Assault Rifle_1.prefab"));
            Animator sourceAnimator = reference.GetComponentInChildren<Animator>(true);
            sourceAnimator.enabled = false;
            bool sampled = false;
            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(sourcePath + "Art/Models/Weapons/Assault Rifle_1/Assault Rifle_1 Animated.fbx"))
                if (asset is AnimationClip clip && (clip.name == "Idle" || clip.name.EndsWith("|Idle", StringComparison.Ordinal)))
                { clip.SampleAnimation(sourceAnimator.gameObject, 0f); sampled = true; break; }
            Require(sampled, "Reference idle clip is missing during hand calibration.");
            Transform sourceLeft = null, sourceRight = null;
            foreach (Transform bone in sourceAnimator.GetComponentsInChildren<Transform>(true))
            {
                if (bone.name == "hand_L.L") sourceLeft = bone;
                if (bone.name == "hand_L.R") sourceRight = bone;
            }
            left = PalmFrame(sourceLeft, "index_L.L", "middle_L.L", "pink_L.L") *
                Quaternion.Inverse(PalmFrame(characterLeft, "index_01_l", "middle_01_l", "pinky_01_l")) * Quaternion.Inverse(leftIkToBone);
            right = PalmFrame(sourceRight, "index_L.R", "middle_L.R", "pink_L.R") *
                Quaternion.Inverse(PalmFrame(characterRight, "index_01_r", "middle_01_r", "pinky_01_r")) * Quaternion.Inverse(rightIkToBone);
        }
        finally { preview.Cleanup(); }
    }

    private static void RenderFrozenSkins(PreviewRenderUtility preview, GameObject root, string path)
    {
        // Repeated renders after Animator.Update in the same editor frame can reuse
        // an earlier GPU skinning result. Snapshot all renderers from the current
        // CPU pose together; this affects disposable captures, never runtime assets.
        var skins = new List<SkinnedMeshRenderer>();
        var snapshots = new List<GameObject>();
        var meshes = new List<Mesh>();
        try
        {
            foreach (SkinnedMeshRenderer skin in root.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (!skin.enabled) continue;
                var mesh = new Mesh();
                skin.BakeMesh(mesh);
                meshes.Add(mesh);
                var snapshot = new GameObject("CPU_" + skin.name, typeof(MeshFilter), typeof(MeshRenderer));
                snapshot.transform.SetParent(skin.transform, false);
                snapshot.GetComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = snapshot.GetComponent<MeshRenderer>();
                renderer.sharedMaterials = skin.sharedMaterials;
                renderer.shadowCastingMode = skin.shadowCastingMode;
                renderer.receiveShadows = skin.receiveShadows;
                snapshots.Add(snapshot);
                skins.Add(skin);
                skin.enabled = false;
            }
            RenderPreviewImage(preview, path);
        }
        finally
        {
            foreach (GameObject snapshot in snapshots) Object.DestroyImmediate(snapshot);
            foreach (Mesh mesh in meshes) Object.DestroyImmediate(mesh);
            foreach (SkinnedMeshRenderer skin in skins) skin.enabled = true;
        }
    }

    private static void RenderPreviewImage(PreviewRenderUtility preview, string path)
    {
        preview.BeginPreview(new Rect(0, 0, 900, 1100), GUIStyle.none);
        preview.Render(true);
        SaveTexture(preview.EndPreview(), path);
    }

    private static PoseMeasurement MeasurePose(GameObject root, string stage)
    {
        Animator animator = root.GetComponent<Animator>();
        Bounds geometry = GeometryBounds(root);
        return new PoseMeasurement { stage = stage, root = root.transform.position,
            hips = animator.GetBoneTransform(HumanBodyBones.Hips).position,
            head = animator.GetBoneTransform(HumanBodyBones.Head).position,
            leftFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot).position,
            rightFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot).position,
            leftShoulder = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm).position,
            rightShoulder = animator.GetBoneTransform(HumanBodyBones.RightUpperArm).position,
            humanScale = animator.humanScale, geometryCenter = geometry.center, geometrySize = geometry.size };
    }

    private static Bounds GeometryBounds(GameObject root, float maximumY = float.PositiveInfinity)
    {
        Bounds bounds = default;
        bool initialized = false;
        var mesh = new Mesh();
        try
        {
            foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (!renderer.enabled || renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly) continue;
                mesh.Clear();
                renderer.BakeMesh(mesh);
                foreach (Vector3 vertex in mesh.vertices)
                {
                    Vector3 point = renderer.transform.TransformPoint(vertex);
                    if (point.y > maximumY) continue;
                    if (!initialized) { bounds = new Bounds(point, Vector3.zero); initialized = true; }
                    else bounds.Encapsulate(point);
                }
            }
        }
        finally { Object.DestroyImmediate(mesh); }
        Require(initialized, "Cannot frame a character without visible skinned geometry.");
        return bounds;
    }

    private static void FrameGeometry(Camera camera, Bounds bounds)
    {
        float verticalHalfAngle = camera.fieldOfView * Mathf.Deg2Rad * 0.5f;
        float halfHeight = Mathf.Max(bounds.extents.y, bounds.extents.x / (900f / 1100f));
        float distance = halfHeight * 1.12f / Mathf.Tan(verticalHalfAngle) + bounds.extents.z;
        camera.transform.position = bounds.center + Vector3.back * distance;
        camera.transform.LookAt(bounds.center);
    }

    private static void SaveTexture(Texture source, string path)
    {
        RenderTexture previous = RenderTexture.active;
        RenderTexture target = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
        var image = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
        try
        {
            Graphics.Blit(source, target);
            RenderTexture.active = target;
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            image.Apply();
            File.WriteAllBytes(path, image.EncodeToPNG());
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            Object.DestroyImmediate(image);
        }
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("[RifleGirl] " + message);
    }
}

// Disposable editor-only callback: supplies the same raw hand goals as gameplay
// without spawning a network player or changing a saved prefab.
[ExecuteAlways]
internal sealed class RifleGirlHandPoseDiagnostic : MonoBehaviour
{
    public Animator animator;
    public Transform leftGrip, rightGrip;
    public Vector3 eye;
    public bool applyGoals;
    public int callbacks;
    public Quaternion leftBaseIk, rightBaseIk, leftBaseBone, rightBaseBone;
    public Quaternion leftRotationOffset = Quaternion.identity, rightRotationOffset = Quaternion.identity;
    private void OnAnimatorIK(int layer)
    {
        if (layer != animator.GetLayerIndex("HandsIK")) return;
        callbacks++;
        leftBaseIk = animator.GetIKRotation(AvatarIKGoal.LeftHand);
        rightBaseIk = animator.GetIKRotation(AvatarIKGoal.RightHand);
        leftBaseBone = animator.GetBoneTransform(HumanBodyBones.LeftHand).rotation;
        rightBaseBone = animator.GetBoneTransform(HumanBodyBones.RightHand).rotation;
        if (!applyGoals) return;
        animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, 1f);
        animator.SetIKRotationWeight(AvatarIKGoal.LeftHand, 1f);
        animator.SetIKPosition(AvatarIKGoal.LeftHand, leftGrip.position);
        animator.SetIKRotation(AvatarIKGoal.LeftHand, leftGrip.rotation * leftRotationOffset);
        animator.SetIKPositionWeight(AvatarIKGoal.RightHand, 1f);
        animator.SetIKRotationWeight(AvatarIKGoal.RightHand, 1f);
        animator.SetIKPosition(AvatarIKGoal.RightHand, rightGrip.position);
        animator.SetIKRotation(AvatarIKGoal.RightHand, rightGrip.rotation * rightRotationOffset);
        animator.SetLookAtWeight(.5f, .35f, .7f, 0f, .6f);
        animator.SetLookAtPosition(eye + Vector3.forward * 10f);
    }
}
