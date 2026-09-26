using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Bakes the Demo's mechanical meshes and contact trajectories, never its FPS hand skeleton.</summary>
public static class PvpWeaponAssetBuilder
{
    private const string SourceRoot = "Assets/PvpWeapons/Source";
    private const string Output = "Assets/Resources/PvpWeapons";
    private static readonly string[] SightNames = { "Iron Sight", "Red Dot 1", "Red Dot 2", "Red Dot 3", "Holographic Sight", "8X Sight" };

    [Serializable] private sealed class Manifest { public SourceWeapon[] weapons = Array.Empty<SourceWeapon>(); }
    [Serializable] private sealed class SourceCue { public float time = 0f; public string path = ""; }
    [Serializable] private sealed class SourceWeapon
    {
        public string definitionJson = "", resourceName = "", sourcePrefab = "", animatedFbx = "", casingPrefab = "";
        public string fireClip = "", reloadClip = "", emptyReloadClip = "", equipClip = "";
        public SourceCue[] fireCues = Array.Empty<SourceCue>(), reloadCues = Array.Empty<SourceCue>(), emptyReloadCues = Array.Empty<SourceCue>(), equipCues = Array.Empty<SourceCue>();
    }
    private sealed class Part
    {
        public Transform source, target;
        public string sourceMeshName;
        public Vector3 sourceLocalPositionOffset;
    }
    [Serializable] private sealed class ValidationReport
    {
        public string generatedUtc;
        public string result = "Editor asset validation passed; runtime visuals and network behavior require play-mode verification.";
        public WeaponReport[] weapons;
    }
    [Serializable] private sealed class WeaponReport
    {
        public string name;
        public int mechanicalRenderers, opticRenderers;
        public Vector3 muzzle, leftGrip, rightGrip, mechanicalBoundsSize;
        public Vector3 ironAimPoint;
        public float roundsPerMinute, shotInterval, damage, range, reloadDuration, equipDuration;
        public int magazineCapacity, reserveAmmo;
        public byte[] sightIds;
        public float[] sightEyeReliefs;
        public Vector3[] ironNearEyePoints;
    }
    [Serializable] private sealed class OpticsReport { public string generatedUtc; public OpticReport[] sights; }
    [Serializable] private sealed class OpticReport
    {
        public string weapon; public int sight; public Vector3 aimPoint; public OpticPartReport[] parts;
    }
    [Serializable] private sealed class OpticPartReport
    {
        public string name, mesh; public Vector3 position, forward, boundsCenter, boundsSize, localMeshCenter, localMeshSize;
        public float nearestCameraDepth; public OpticMaterialReport[] materials;
    }
    [Serializable] private sealed class OpticMaterialReport
    {
        public string name, shader; public int queue; public float scale, flipVisibleSide, cameraSide;
        public Vector2 centerUv; public Color tint;
    }

    // Read-only geometry/material diagnostics: usable with -executeMethod before changing placement.
    public static void DiagnoseOptics()
    {
        var reports = new List<OpticReport>();
        foreach (string weapon in new[] { "AssaultRifle", "Pistol", "Sniper" })
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(Output + "/Models/" + weapon + ".prefab").GetComponent<PvpWeaponModel>();
            foreach (var sight in model.sights)
            {
                Vector3 camera = sight.aimPoint.position - Vector3.forward * Mathf.Max(0.12f, sight.eyeRelief);
                var parts = new List<OpticPartReport>();
                foreach (var renderer in sight.visual.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
                    var materials = new List<OpticMaterialReport>();
                    foreach (var material in renderer.sharedMaterials)
                    {
                        Vector3 origin = renderer.transform.position, normal = renderer.transform.forward;
                        float distance = material.HasProperty("_ProjectionDistance") ? material.GetFloat("_ProjectionDistance") : 100f;
                        float scale = material.HasProperty("_Scale") ? material.GetFloat("_Scale") : 1f;
                        Vector3 plane = origin + normal * distance;
                        Vector3 intersection = camera + Vector3.forward * (Vector3.Dot(plane - camera, normal) / Vector3.Dot(Vector3.forward, normal));
                        Vector3 relative = intersection - plane;
                        materials.Add(new OpticMaterialReport { name = material.name, shader = material.shader.name, queue = material.renderQueue,
                            scale = scale, flipVisibleSide = material.HasProperty("_FlipVisibleSide") ? material.GetFloat("_FlipVisibleSide") : 0f,
                            cameraSide = Vector3.Dot(camera - origin, normal),
                            centerUv = new Vector2(Vector3.Dot(relative, renderer.transform.right), Vector3.Dot(relative, renderer.transform.up)) / (scale * distance) + Vector2.one * 0.5f,
                            tint = material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white });
                    }
                    parts.Add(new OpticPartReport { name = renderer.name, mesh = mesh.name, position = renderer.transform.position,
                        forward = renderer.transform.forward, boundsCenter = renderer.bounds.center, boundsSize = renderer.bounds.size,
                        localMeshCenter = mesh.bounds.center, localMeshSize = mesh.bounds.size,
                        nearestCameraDepth = renderer.bounds.min.z - camera.z, materials = materials.ToArray() });
                }
                reports.Add(new OpticReport { weapon = weapon, sight = sight.sightId, aimPoint = sight.aimPoint.position, parts = parts.ToArray() });
            }
        }
        Directory.CreateDirectory("Logs/PvpMigration");
        File.WriteAllText("Logs/PvpMigration/weapon-optics.json", JsonUtility.ToJson(new OpticsReport { generatedUtc = DateTime.UtcNow.ToString("O"), sights = reports.ToArray() }, true));
    }

    [MenuItem("联机/生成 Demo PVP 枪械资源")]
    public static void Build()
    {
        string manifestPath = SourceRoot + "/demo-reference.json";
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("Demo weapon reference is missing", manifestPath);
        EnsureFolder(Output + "/Definitions");
        EnsureFolder(Output + "/Models");
        EnsureFolder(Output + "/Animations");
        EnsureFolder(Output + "/Meshes");
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(manifestPath));
        if (manifest == null || manifest.weapons == null || manifest.weapons.Length != 3)
            throw new InvalidOperationException("Expected AssaultRifle, Pistol, and Sniper references.");
        FixImportedMaterials();
        BuildImpacts();
        foreach (var source in manifest.weapons) BuildWeapon(source);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Validate();
        Debug.Log("[PVP Weapons] Built three mechanical-only weapons and Demo definitions; no FPS hands or framework scripts remain in output prefabs.");
    }

    private static void BuildWeapon(SourceWeapon data)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(data.sourcePrefab);
        if (prefab == null) throw new InvalidOperationException("Missing source weapon " + data.sourcePrefab);
        string definitionPath = Output + "/Definitions/" + data.resourceName + ".asset";
        var definition = AssetDatabase.LoadAssetAtPath<PvpWeaponDefinition>(definitionPath);
        if (definition == null)
        {
            definition = ScriptableObject.CreateInstance<PvpWeaponDefinition>();
            AssetDatabase.CreateAsset(definition, definitionPath);
        }
        JsonUtility.FromJsonOverwrite(data.definitionJson, definition);
        definition.fireClip = LoadAudio(data.fireClip);
        definition.reloadClip = LoadAudio(data.reloadClip);
        definition.emptyReloadClip = LoadAudio(data.emptyReloadClip);
        definition.equipClip = LoadAudio(data.equipClip);
        definition.fireCues = LoadCues(data.fireCues);
        definition.reloadCues = LoadCues(data.reloadCues);
        definition.emptyReloadCues = LoadCues(data.emptyReloadCues);
        definition.equipCues = LoadCues(data.equipCues);
        definition.headDamageMultiplier = 2.5f;
        definition.limbDamageMultiplier = 1f;
        definition.motion = new PvpMotionDefinition();
        if (definition.weaponId != 0)
        {
            definition.motion.walkPosition = Vector3.one * 0.0005f;
            definition.motion.walkRotation = new Vector3(0.07f, 0f, -0.3f);
            definition.motion.walkRotationFrequency = new Vector3(definition.weaponId == 2 ? 1f : 2f, 0f, 1f);
        }
        if (definition.weaponId == 1)
        {
            definition.motion.sprintPosition = new Vector3(-0.05f, -0.08f, -0.04f);
            definition.motion.sprintRotation = new Vector3(12.4f, -15.52f, 41.6f);
        }
        definition.hipKick.springSpeedMultiplier = definition.weaponId == 2 ? 0.87f : 1f;
        definition.aimKick.springSpeedMultiplier = 1f;
        definition.damageOverRange = new AnimationCurve(new Keyframe(0f, 1f, 0f, 0f), new Keyframe(1f, 0.01f, 0f, 0f));
        byte[] supported = definition.weaponId == 1 ? new byte[] { 0, 3 } : new byte[] { 0, 1, 2, 3, 4, 5 };
        definition.sights = supported.Select(id => new PvpSightDefinition
        {
            sightId = id, displayName = id == 0 ? "机瞄" : id == 4 ? "全息" : id == 5 ? "8 倍镜" : "红点 " + id,
            fieldOfView = id == 5 ? 15f : 55f,
            referenceWeaponFieldOfView = id == 5 ? 15f : id == 2 ? 40f : id == 4 ? 45f : definition.weaponId == 0 ? 50f : 55f,
            magnified = id == 5, secondaryFieldOfView = id == 5 ? 55f : 0f
        }).ToArray();

        GameObject source = Object.Instantiate(prefab);
        source.name = prefab.name;
        source.SetActive(true);
        GameObject output = new GameObject(data.resourceName);
        try
        {
            var sourceAnimator = source.GetComponentInChildren<Animator>(true);
            if (sourceAnimator == null) throw new InvalidOperationException("Source has no animation bake root: " + data.sourcePrefab);
            Transform animationRoot = sourceAnimator.transform;
            sourceAnimator.enabled = false;
            var clips = AssetDatabase.LoadAllAssetsAtPath(data.animatedFbx).OfType<AnimationClip>()
                .Where(clip => !clip.name.StartsWith("__preview__", StringComparison.Ordinal)).ToArray();
            var idle = FindClip(clips, "Idle");
            if (idle != null) idle.SampleAnimation(animationRoot.gameObject, 0f);
            Transform rightHand = Find(animationRoot, "hand_L.R");
            Transform leftHand = Find(animationRoot, "hand_L.L");
            if (rightHand == null || leftHand == null) throw new InvalidOperationException("Demo contact bones not found for " + data.resourceName);
            var frame = new GameObject("BakeFrame").transform;
            // The source prefab itself is authored in the view direction (+Z, Y up).
            frame.SetPositionAndRotation(rightHand.position, source.transform.rotation);
            Transform gunBone = animationRoot.GetComponentsInChildren<Transform>(true)
                .FirstOrDefault(t => t.name == prefab.name && t != animationRoot && t.parent != animationRoot);
            frame.SetParent(gunBone != null ? gunBone : rightHand, true);

            var model = output.AddComponent<PvpWeaponModel>();
            // Shared RifleGirl arm reach differs from the Demo's isolated FPS-hand rig.
            // Keep mechanical dimensions/contact coordinates intact; adjust only the held placement.
            model.neutralPosition = new Vector3(0f, 0f, definition.weaponId == 0 ? -0.07f : definition.weaponId == 1 ? 0.15f : -0.12f);
            var parts = new List<Part>();
            var sightGroups = new Dictionary<byte, GameObject>();
            foreach (byte id in supported)
            {
                var group = new GameObject("Sight_" + id);
                group.transform.SetParent(output.transform, false);
                sightGroups[id] = group;
            }
            var sightRenderers = new Dictionary<byte, List<Renderer>>();
            foreach (byte id in supported) sightRenderers[id] = new List<Renderer>();
            int meshIndex = 0;
            foreach (var renderer in source.GetComponentsInChildren<MeshRenderer>(true))
            {
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                if (IsExcluded(renderer.transform, source.transform)) continue;
                int sightId = GetSightId(renderer.transform, source.transform);
                if (sightId < 0 && !renderer.gameObject.activeInHierarchy) continue;
                if (sightId >= 0 && !sightGroups.ContainsKey((byte)sightId)) continue;
                var go = new GameObject("Part_" + meshIndex++ + "_" + renderer.name);
                go.transform.SetParent(sightId >= 0 ? sightGroups[(byte)sightId].transform : output.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                var meshRenderer = go.AddComponent<MeshRenderer>();
                meshRenderer.sharedMaterials = OpticMaterials(renderer, sightId);
                bool lensSurface = meshRenderer.sharedMaterials.Any(m => m != null && (m.shader.name == "Lens" || m.name == "PvpOpticGlass"));
                meshRenderer.shadowCastingMode = lensSurface ? UnityEngine.Rendering.ShadowCastingMode.Off : UnityEngine.Rendering.ShadowCastingMode.On;
                meshRenderer.receiveShadows = !lensSurface;
                SetFramePose(go.transform, renderer.transform, frame);
                parts.Add(new Part { source = renderer.transform, target = go.transform, sourceMeshName = renderer.name });
                if (sightId >= 0) sightRenderers[(byte)sightId].Add(meshRenderer);
                if (renderer.name.Equals("Magazine", StringComparison.OrdinalIgnoreCase)) model.magazine = go.transform;
                if (renderer.name.Equals("Bolt", StringComparison.OrdinalIgnoreCase)) model.bolt = go.transform;
            }
            foreach (var renderer in source.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.sharedMesh == null || IsExcluded(renderer.transform, source.transform) || !renderer.gameObject.activeInHierarchy) continue;
                int sightId = GetSightId(renderer.transform, source.transform);
                Transform parent = sightId >= 0 && sightGroups.TryGetValue((byte)sightId, out var sightGroup) ? sightGroup.transform : output.transform;
                BakeMechanicalSkin(renderer, frame, parent, model, parts, data.resourceName, ref meshIndex);
            }
            if (meshIndex == 0) throw new InvalidOperationException("No mechanical meshes extracted from " + data.resourceName);
            model.rightGrip = NewPoint(output.transform, "RightGrip", frame.InverseTransformPoint(rightHand.position), Quaternion.Inverse(frame.rotation) * rightHand.rotation);
            model.leftGrip = NewPoint(output.transform, "LeftGrip", frame.InverseTransformPoint(leftHand.position), Quaternion.Inverse(frame.rotation) * leftHand.rotation);
            model.reloadHandTarget = NewPoint(output.transform, "ReloadHandTarget", model.leftGrip.localPosition, model.leftGrip.localRotation);
            parts.Add(new Part { source = leftHand, target = model.reloadHandTarget });
            Transform sourceMuzzle = FindMuzzle(source.transform);
            Transform sourceEjection = Find(source.transform, "Ejection Port") ?? Find(source.transform, "Casing Ejection Port");
            model.muzzle = NewPoint(output.transform, "Muzzle", sourceMuzzle != null ? frame.InverseTransformPoint(sourceMuzzle.position) : new Vector3(0f, 0.08f, 0.5f), Quaternion.identity);
            model.ejectionPort = NewPoint(output.transform, "EjectionPort", sourceEjection != null ? frame.InverseTransformPoint(sourceEjection.position) : new Vector3(0.04f, 0.08f, 0.04f), Quaternion.identity);
            var sightModels = new List<PvpSightModel>();
            foreach (byte id in supported)
            {
                var lens = sightRenderers[id].FirstOrDefault(r => r.sharedMaterials.Any(m => m != null && (m.name.Contains("Reticle") || m.name.Contains("Retical"))));
                if (lens == null) lens = sightRenderers[id].FirstOrDefault(r => r.name.IndexOf("Glass", StringComparison.OrdinalIgnoreCase) >= 0);
                Vector3 center;
                if (lens != null)
                {
                    center = output.transform.InverseTransformPoint(lens.bounds.center);
                    if (definition.weaponId == 2 && id == 1)
                    {
                        // This weapon's inherited lens was 13 mm below the physical hole.
                        // Keep its depth/scale, move the reticle and aim axis to the real rim.
                        var housing = sightRenderers[id].Single(r => r.name.EndsWith("_Red Dot Sight", StringComparison.Ordinal));
                        Vector3 rim = output.transform.InverseTransformPoint(RedDotOneApertureCenter(housing));
                        Vector3 corrected = new Vector3(rim.x, rim.y, center.z);
                        var lensPart = parts.Single(p => p.target == lens.transform);
                        lensPart.sourceLocalPositionOffset = lensPart.source.InverseTransformVector(frame.TransformVector(corrected - center));
                        lens.transform.position += output.transform.TransformVector(corrected - center);
                        center = corrected;
                    }
                }
                else if (id == 0)
                {
                    center = ReferenceIronAimPoint(definition.weaponId, source.transform, frame, output.transform, parts);
                }
                else
                {
                    var first = sightRenderers[id].FirstOrDefault();
                    center = first != null ? output.transform.InverseTransformPoint(first.bounds.center) : new Vector3(0f, 0.1f, 0.05f);
                }
                // Reticle center supplies the line of sight. Long tube sights require more
                // clearance than open reflex sights when sharing the character's world camera.
                var point = NewPoint(output.transform, "AimPoint_" + id, center, Quaternion.identity);
                float eyeRelief = 0.12f;
                if (id != 0 && sightRenderers[id].Count > 0)
                {
                    float rear = sightRenderers[id].Min(r => output.transform.InverseTransformPoint(r.bounds.min).z);
                    eyeRelief = Mathf.Max(eyeRelief, Mathf.Ceil((center.z - rear + 0.065f) / 0.005f) * 0.005f);
                }
                Vector3[] nearEyePoints = definition.weaponId == 2 && id == 0
                    ? SniperReceiverNearEyePoints(output.transform, center) : Array.Empty<Vector3>();
                sightModels.Add(new PvpSightModel { sightId = id, visual = sightGroups[id], aimPoint = point,
                    eyeRelief = eyeRelief, nearEyePoints = nearEyePoints });
            }
            model.sights = sightModels.ToArray();
            model.casingPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(data.casingPrefab);
            model.muzzleEffects = BuildEffects(source.transform, output.transform, frame);
            model.mechanicalAnimation = output.AddComponent<Animation>();
            model.mechanicalAnimation.playAutomatically = false;
            model.mechanicalAnimation.cullingType = AnimationCullingType.AlwaysAnimate;
            model.idleAnimation = BakeClip(idle, "Idle", data.resourceName, animationRoot, frame, parts, output);
            var fire = FindClip(clips, definition.weaponId == 2 ? "Recharge" : "Fire");
            model.fireAnimation = BakeClip(fire, "Fire", data.resourceName, animationRoot, frame, parts, output);
            model.reloadAnimation = BakeClip(FindClip(clips, "Reload"), "Reload", data.resourceName, animationRoot, frame, parts, output);
            var equip = FindClip(clips, "Take");
            model.equipAnimation = BakeClip(equip, "Equip", data.resourceName, animationRoot, frame, parts, output);
            if (equip != null) definition.equipDuration = equip.length;
            definition.actionCycleDuration = definition.weaponId == 2 && fire != null ? fire.length : 0f;
            if (model.idleAnimation != null) model.idleAnimation.SampleAnimation(output, 0f);
            model.SelectSight(definition.weaponId == 2 ? (byte)5 : (byte)0);
            definition.modelPrefab = PrefabUtility.SaveAsPrefabAsset(output, Output + "/Models/" + data.resourceName + ".prefab");
            EditorUtility.SetDirty(definition);
        }
        finally
        {
            Object.DestroyImmediate(source);
            Object.DestroyImmediate(output);
        }
    }

    private static Vector3 ReferenceIronAimPoint(byte weaponId, Transform sourceRoot, Transform frame, Transform output, List<Part> parts)
    {
        // The AR has separate rear/front sight skins; the pistol's notch is part of Top.
        // The reference sniper has no rear-sight mesh at all: its Iron Sight attachment is
        // a bare-receiver aiming preset. Reconstruct the actual authored camera axis for all
        // three, using real mechanical geometry only to select a useful eye-relief plane.
        string requiredMesh = weaponId == 0 ? "Rear Sight" : weaponId == 1 ? "Top" : "Bolt";
        var anchor = parts.FirstOrDefault(p => string.Equals(p.sourceMeshName, requiredMesh, StringComparison.OrdinalIgnoreCase));
        if (anchor == null || anchor.target.GetComponent<MeshRenderer>() == null)
            throw new InvalidOperationException("Cannot derive iron aim point; source mechanical mesh is missing: " + requiredMesh);
        Bounds geometry = anchor.target.GetComponent<MeshRenderer>().bounds;
        float depth = output.InverseTransformPoint(weaponId == 1
            ? new Vector3(geometry.center.x, geometry.center.y, geometry.min.z + 0.02f)
            : geometry.center).z;
        Transform procedural = Find(sourceRoot, "Transforms");
        if (procedural == null) throw new InvalidOperationException("Missing source procedural root for iron ADS.");
        Vector3 savedPosition = procedural.localPosition;
        Quaternion savedRotation = procedural.localRotation;
        // AttachmentSight.presets[0] from the three original Demo weapon prefabs.
        Vector3 offset = weaponId == 0 ? new Vector3(-0.08f, 0.023f, -0.1f) :
            weaponId == 1 ? new Vector3(-0.06f, 0.044f, 0.02f) : new Vector3(-0.074f, 0.06f, -0.05f);
        Vector3 rotationOffset = weaponId == 0 ? new Vector3(0f, 0f, -4f) : Vector3.zero;
        try
        {
            procedural.localPosition = savedPosition + offset;
            procedural.localRotation = Quaternion.Euler(savedRotation.eulerAngles + rotationOffset);
            Vector3 origin = frame.InverseTransformPoint(sourceRoot.position);
            Vector3 direction = frame.InverseTransformDirection(sourceRoot.forward).normalized;
            if (Mathf.Abs(direction.z) < 0.5f) throw new InvalidOperationException("Source iron aiming axis is not forward.");
            return origin + direction * ((depth - origin.z) / direction.z);
        }
        finally { procedural.localPosition = savedPosition; procedural.localRotation = savedRotation; }
    }

    private static Vector3 RedDotOneApertureCenter(Renderer housing)
    {
        var mesh = housing.GetComponent<MeshFilter>().sharedMesh;
        // The supplied Red Dot mesh has its circular front bevel at local Z=91.68 mm.
        // Select this complete rim (not the receiver beneath it) and derive its axis
        // from opposite extrema; do not use the housing's overall bounds center.
        var rim = mesh.vertices.Where(v => v.z > 0.09167f && v.z < 0.09169f).ToArray();
        if (rim.Length < 16) throw new InvalidOperationException("Red Dot 1 front rim geometry changed; recalibrate its optical axis.");
        var bounds = new Bounds(rim[0], Vector3.zero);
        foreach (Vector3 point in rim) bounds.Encapsulate(point);
        if (bounds.size.x < 0.03f || bounds.size.y < 0.03f)
            throw new InvalidOperationException("Red Dot 1 aperture could not be resolved from its front rim.");
        return housing.transform.TransformPoint(bounds.center);
    }

    private static Vector3[] SniperReceiverNearEyePoints(Transform model, Vector3 aimPoint)
    {
        var filter = model.GetComponentsInChildren<MeshFilter>(true).Single(f => f.sharedMesh != null && f.sharedMesh.name == "Sniper_Body_Sniper_1");
        // Only the high rear receiver edge can cross the bare aiming view. The stock
        // extends behind the eye normally and must not affect this constraint.
        var upperReceiver = filter.sharedMesh.vertices.Select(v => model.InverseTransformPoint(filter.transform.TransformPoint(v)))
            .Where(v => v.y >= aimPoint.y - 0.02f && v.z <= aimPoint.z && v.z >= aimPoint.z - 0.15f).ToArray();
        if (upperReceiver.Length < 3) throw new InvalidOperationException("Sniper rear receiver edge was not found.");
        float rear = upperReceiver.Min(v => v.z);
        var edge = upperReceiver.Where(v => v.z <= rear + 0.002f).Distinct().ToArray();
        var points = new[] { edge.OrderBy(v => v.z).First(), edge.OrderBy(v => v.x).First(),
            edge.OrderByDescending(v => v.x).First(), edge.OrderByDescending(v => v.y).First() }.Distinct().ToArray();
        if (points.Length < 3 || aimPoint.z - rear < 0.03f || aimPoint.z - rear > 0.12f)
            throw new InvalidOperationException("Sniper rear receiver geometry changed; check bare-sight clearance.");
        return points;
    }

    private static void BakeMechanicalSkin(SkinnedMeshRenderer renderer, Transform frame, Transform output,
        PvpWeaponModel model, List<Part> parts, string weaponName, ref int meshIndex)
    {
        // The source FBX skins its rigid receiver, magazine and bolt as well as its hands.
        // Split only weapon-material triangles into rigid mechanical pieces; no skeleton survives.
        var sourceMesh = renderer.sharedMesh;
        {
            Vector3[] vertices = sourceMesh.vertices, normals = sourceMesh.normals;
            Vector2[] uv = sourceMesh.uv;
            Vector4[] tangents = sourceMesh.tangents;
            Matrix4x4[] bindPoses = sourceMesh.bindposes;
            BoneWeight[] weights = renderer.sharedMesh.boneWeights;
            Transform[] bones = renderer.bones;
            Material[] materials = renderer.sharedMaterials;
            for (int submesh = 0; submesh < sourceMesh.subMeshCount; submesh++)
            {
                Material material = submesh < materials.Length ? materials[submesh] : null;
                string materialName = material != null ? material.name.ToLowerInvariant() : "";
                if (materialName.Contains("arm") || materialName.Contains("hand")) continue;
                var groups = new Dictionary<int, List<int>>();
                int[] triangles = sourceMesh.GetTriangles(submesh);
                for (int t = 0; t < triangles.Length; t += 3)
                {
                    int index = triangles[t];
                    int boneIndex = index < weights.Length ? DominantBone(weights[index]) : -1;
                    if (!groups.TryGetValue(boneIndex, out var group)) groups[boneIndex] = group = new List<int>();
                    group.Add(triangles[t]); group.Add(triangles[t + 1]); group.Add(triangles[t + 2]);
                }
                foreach (var group in groups)
                {
                    Transform bone = group.Key >= 0 && group.Key < bones.Length && bones[group.Key] != null ? bones[group.Key] : renderer.transform;
                    var remap = new Dictionary<int, int>();
                    var newVertices = new List<Vector3>(); var newNormals = new List<Vector3>();
                    var newUv = new List<Vector2>(); var newTangents = new List<Vector4>(); var newTriangles = new List<int>();
                    // Skinning is bone.localToWorld * bindPose * sourceVertex. Convert directly to
                    // bone space: BakeMesh may already include FBX scale compensation and must not
                    // be transformed through the scaled renderer a second time.
                    Matrix4x4 toBone = group.Key >= 0 && group.Key < bindPoses.Length ? bindPoses[group.Key] : bone.worldToLocalMatrix * renderer.localToWorldMatrix;
                    Matrix4x4 normalToBone = toBone.inverse.transpose;
                    foreach (int oldIndex in group.Value)
                    {
                        if (!remap.TryGetValue(oldIndex, out int newIndex))
                        {
                            newIndex = newVertices.Count; remap.Add(oldIndex, newIndex);
                            newVertices.Add(toBone.MultiplyPoint3x4(vertices[oldIndex]));
                            if (normals.Length == vertices.Length) newNormals.Add(normalToBone.MultiplyVector(normals[oldIndex]).normalized);
                            if (uv.Length == vertices.Length) newUv.Add(uv[oldIndex]);
                            if (tangents.Length == vertices.Length)
                            {
                                Vector4 tangent = tangents[oldIndex]; Vector3 direction = toBone.MultiplyVector(new Vector3(tangent.x, tangent.y, tangent.z)).normalized;
                                newTangents.Add(new Vector4(direction.x, direction.y, direction.z, tangent.w));
                            }
                        }
                        newTriangles.Add(newIndex);
                    }
                    var mesh = new Mesh { name = weaponName + "_" + renderer.name + "_" + bone.name };
                    if (newVertices.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                    mesh.SetVertices(newVertices); mesh.SetTriangles(newTriangles, 0);
                    if (newNormals.Count > 0) mesh.SetNormals(newNormals); else mesh.RecalculateNormals();
                    if (newUv.Count > 0) mesh.SetUVs(0, newUv);
                    if (newTangents.Count > 0) mesh.SetTangents(newTangents); else if (newUv.Count > 0) mesh.RecalculateTangents();
                    mesh.RecalculateBounds();
                    string meshPath = Output + "/Meshes/" + weaponName + "_Mechanical_" + meshIndex + ".asset";
                    var existing = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
                    if (existing != null) { EditorUtility.CopySerialized(mesh, existing); Object.DestroyImmediate(mesh); mesh = existing; }
                    else AssetDatabase.CreateAsset(mesh, meshPath);
                    var go = new GameObject("Mechanical_" + meshIndex++ + "_" + bone.name);
                    go.transform.SetParent(output, false);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    go.AddComponent<MeshRenderer>().sharedMaterial = material;
                    SetFramePose(go.transform, bone, frame);
                    parts.Add(new Part { source = bone, target = go.transform, sourceMeshName = renderer.name });
                    string partName = (renderer.name + " " + bone.name).ToLowerInvariant();
                    if (partName.Contains("magazine") || partName.Contains("mag_")) model.magazine = go.transform;
                    if (partName.Contains("bolt") || partName.Contains("slide")) model.bolt = go.transform;
                }
            }
        }
    }

    private static int DominantBone(BoneWeight weight)
    {
        int index = weight.boneIndex0; float largest = weight.weight0;
        if (weight.weight1 > largest) { index = weight.boneIndex1; largest = weight.weight1; }
        if (weight.weight2 > largest) { index = weight.boneIndex2; largest = weight.weight2; }
        if (weight.weight3 > largest) index = weight.boneIndex3;
        return index;
    }

    private static AnimationClip BakeClip(AnimationClip sourceClip, string label, string weapon,
        Transform animationRoot, Transform frame, List<Part> parts, GameObject output)
    {
        if (sourceClip == null) return null;
        var clip = new AnimationClip { name = label, legacy = true, frameRate = 60f, wrapMode = WrapMode.ClampForever };
        var curves = new AnimationCurve[parts.Count, 10];
        for (int p = 0; p < parts.Count; p++) for (int c = 0; c < 10; c++) curves[p, c] = new AnimationCurve();
        int frames = Mathf.Max(1, Mathf.CeilToInt(sourceClip.length * 60f));
        for (int f = 0; f <= frames; f++)
        {
            float time = Mathf.Min(f / 60f, sourceClip.length);
            sourceClip.SampleAnimation(animationRoot.gameObject, time);
            for (int p = 0; p < parts.Count; p++)
            {
                var part = parts[p];
                Vector3 position = frame.InverseTransformPoint(part.source.TransformPoint(part.sourceLocalPositionOffset));
                Quaternion rotation = Quaternion.Inverse(frame.rotation) * part.source.rotation;
                Vector3 scale = part.source.lossyScale;
                float[] values = { position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w, scale.x, scale.y, scale.z };
                for (int c = 0; c < 10; c++) curves[p, c].AddKey(time, values[c]);
            }
        }
        string[] properties = { "localPosition.x", "localPosition.y", "localPosition.z", "localRotation.x", "localRotation.y", "localRotation.z", "localRotation.w", "localScale.x", "localScale.y", "localScale.z" };
        for (int p = 0; p < parts.Count; p++)
        {
            string relative = AnimationUtility.CalculateTransformPath(parts[p].target, output.transform);
            for (int c = 0; c < properties.Length; c++) clip.SetCurve(relative, typeof(Transform), properties[c], curves[p, c]);
        }
        clip.EnsureQuaternionContinuity();
        AnimationUtility.SetAnimationEvents(clip, Array.Empty<AnimationEvent>());
        string clipPath = Output + "/Animations/" + weapon + "_" + label + ".anim";
        var old = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
        if (old != null) { EditorUtility.CopySerialized(clip, old); Object.DestroyImmediate(clip); clip = old; }
        else AssetDatabase.CreateAsset(clip, clipPath);
        output.GetComponent<Animation>().AddClip(clip, label);
        return clip;
    }

    private static ParticleSystem[] BuildEffects(Transform source, Transform target, Transform frame)
    {
        var effects = new List<ParticleSystem>();
        foreach (var original in source.GetComponentsInChildren<ParticleSystem>(true))
        {
            if (!original.gameObject.activeInHierarchy || IsExcluded(original.transform, source)) continue;
            if (original.transform.parent != null && original.transform.parent.GetComponentInParent<ParticleSystem>() != null) continue;
            var clone = Object.Instantiate(original.gameObject, target, false);
            clone.name = "MuzzleEffect_" + effects.Count;
            SetFramePose(clone.transform, original.transform, frame);
            foreach (var behaviour in clone.GetComponentsInChildren<MonoBehaviour>(true)) Object.DestroyImmediate(behaviour);
            foreach (var effect in clone.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = effect.main;
                main.playOnAwake = false;
                main.loop = false;
                effect.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
            effects.Add(clone.GetComponent<ParticleSystem>());
        }
        return effects.ToArray();
    }

    private static int GetSightId(Transform part, Transform root)
    {
        for (var current = part; current != null && current != root; current = current.parent)
        {
            for (int i = 0; i < SightNames.Length; i++) if (current.name == SightNames[i]) return i;
            if (current.name == "Holo Sight") return 4;
            if (current.name == "8X Scope") return 5;
        }
        return -1;
    }

    private static bool IsExcluded(Transform part, Transform root)
    {
        for (var current = part; current != null && current != root; current = current.parent)
        {
            string name = current.name.ToLowerInvariant();
            if (name.Contains("suppressor") || name == "laser" || name.Contains("laser sight") || name == "fps hands" || name.Contains("no stock")) return true;
        }
        return false;
    }

    private static Transform FindMuzzle(Transform source)
    {
        // Setup/Muzzle is the firing transform; attachment category "Muzzle" is a separate object.
        return source.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Muzzle" && t.parent != null && t.parent.name == "Setup")
            ?? source.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Muzzle" && t.GetComponent<ParticleSystem>() == null);
    }

    private static Transform Find(Transform root, string name) => root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == name);
    private static AnimationClip FindClip(AnimationClip[] clips, string suffix) => clips.FirstOrDefault(c => c.name.EndsWith("|" + suffix, StringComparison.OrdinalIgnoreCase));
    private static AudioClip LoadAudio(string path) => string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<AudioClip>(path);
    private static PvpAudioCue[] LoadCues(SourceCue[] cues) => cues == null ? Array.Empty<PvpAudioCue>() : cues.Select(c => new PvpAudioCue { time = c.time, clip = LoadAudio(c.path) }).ToArray();

    private static Transform NewPoint(Transform parent, string name, Vector3 position, Quaternion rotation)
    {
        var point = new GameObject(name).transform;
        point.SetParent(parent, false);
        point.localPosition = position;
        point.localRotation = rotation;
        return point;
    }

    private static void SetFramePose(Transform target, Transform source, Transform frame)
    {
        target.localPosition = frame.InverseTransformPoint(source.position);
        target.localRotation = Quaternion.Inverse(frame.rotation) * source.rotation;
        target.localScale = source.lossyScale;
    }

    private static void FixImportedMaterials()
    {
        foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { SourceRoot }))
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
            if (material == null) continue;
            if (material.shader == null || material.shader.name == "Hidden/InternalErrorShader")
            {
                bool particle = material.name.Contains("Muzzle");
                material.shader = Shader.Find(particle ? "Universal Render Pipeline/Particles/Unlit" : "Universal Render Pipeline/Lit");
                EditorUtility.SetDirty(material);
            }
        }
        // The reference dot occupies only 2-3 pixels at the shared camera's 55-degree
        // FOV. Keep the same reticle texture and parallax, but give it a legible diameter.
        var referenceDot = AssetDatabase.LoadAssetAtPath<Material>(SourceRoot + "/Art/Materials/Optics/Reticle 3.mat");
        if (referenceDot == null || referenceDot.shader.name != "Lens") throw new InvalidOperationException("Missing red-dot lens material.");
        EnsureFolder(Output + "/Materials");
        string dotPath = Output + "/Materials/PvpRedDotReticle.mat";
        var dot = AssetDatabase.LoadAssetAtPath<Material>(dotPath);
        if (dot == null)
        {
            dot = new Material(referenceDot);
            AssetDatabase.CreateAsset(dot, dotPath);
        }
        else dot.CopyPropertiesFromMaterial(referenceDot);
        dot.name = "PvpRedDotReticle";
        dot.SetFloat("_Scale", 0.008f);
        EditorUtility.SetDirty(dot);
        string glassPath = Output + "/Materials/PvpOpticGlass.mat";
        var glass = AssetDatabase.LoadAssetAtPath<Material>(glassPath);
        if (glass == null)
        {
            glass = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = "PvpOpticGlass" };
            AssetDatabase.CreateAsset(glass, glassPath);
        }
        glass.SetFloat("_Surface", 1f);
        glass.SetFloat("_Blend", 0f);
        glass.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        glass.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        glass.SetFloat("_ZWrite", 0f);
        glass.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
        glass.SetColor("_BaseColor", new Color(0.55f, 0.75f, 0.9f, 0.035f));
        glass.SetOverrideTag("RenderType", "Transparent");
        glass.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        glass.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent - 1;
        EditorUtility.SetDirty(glass);
    }

    private static Material[] OpticMaterials(MeshRenderer renderer, int sightId)
    {
        // This imported FBX exposes both glass submeshes with the gun atlas material;
        // the original prefab's material overrides no longer resolve to these file IDs.
        if (sightId == 2 && renderer.name.Equals("Red Dot 2 Glass", StringComparison.OrdinalIgnoreCase))
        {
            var glass = AssetDatabase.LoadAssetAtPath<Material>(Output + "/Materials/PvpOpticGlass.mat");
            return Enumerable.Repeat(glass, renderer.sharedMaterials.Length).ToArray();
        }
        return renderer.sharedMaterials.Select(m => m != null && m.name == "Reticle 3"
            ? AssetDatabase.LoadAssetAtPath<Material>(Output + "/Materials/PvpRedDotReticle.mat") : m).ToArray();
    }

    private static void BuildImpacts()
    {
        EnsureFolder(Output + "/Effects");
        foreach (string name in new[] { "Stone", "Metal", "Wood", "Blood" })
        {
            string path = SourceRoot + "/Prefabs/Particle Systems/Bullet Impact/" + name + " Impact.prefab";
            var original = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (original == null) throw new InvalidOperationException("Missing Demo impact " + path);
            var effect = Object.Instantiate(original);
            try
            {
                effect.name = name + "Impact";
                foreach (var transform in effect.GetComponentsInChildren<Transform>(true))
                {
                    GameObjectUtility.RemoveMonoBehavioursWithMissingScript(transform.gameObject);
                    transform.gameObject.layer = 2;
                }
                foreach (var component in effect.GetComponentsInChildren<MonoBehaviour>(true)) Object.DestroyImmediate(component);
                foreach (var collider in effect.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(collider);
                foreach (var particle in effect.GetComponentsInChildren<ParticleSystem>(true))
                {
                    var main = particle.main;
                    main.playOnAwake = false;
                    main.loop = false;
                    particle.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                }
                PrefabUtility.SaveAsPrefabAsset(effect, Output + "/Effects/" + name + "Impact.prefab");
            }
            finally { Object.DestroyImmediate(effect); }
        }
    }

    private static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;
        string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
        if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
    }

    public static void Validate()
    {
        int weaponIndex = 0;
        var reports = new List<WeaponReport>();
        foreach (string name in new[] { "AssaultRifle", "Pistol", "Sniper" })
        {
            var definition = AssetDatabase.LoadAssetAtPath<PvpWeaponDefinition>(Output + "/Definitions/" + name + ".asset");
            if (definition == null || definition.modelPrefab == null) throw new InvalidOperationException("Missing PVP weapon output: " + name);
            var model = definition.modelPrefab.GetComponent<PvpWeaponModel>();
            if (model == null || model.muzzle == null || model.leftGrip == null || model.rightGrip == null || model.sights == null)
                throw new InvalidOperationException("Incomplete contact points: " + name);
            if (definition.modelPrefab.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length != 0 || definition.modelPrefab.GetComponentsInChildren<Animator>(true).Length != 0)
                throw new InvalidOperationException("Character skeleton leaked into mechanical weapon: " + name);
            if (model.fireAnimation == null || model.reloadAnimation == null || definition.fireClip == null)
                throw new InvalidOperationException("Missing mechanical animation/audio: " + name);
            if (!model.GetComponentsInChildren<MeshRenderer>(true).Any(r => r.name.StartsWith("Mechanical_", StringComparison.Ordinal)))
                throw new InvalidOperationException("The weapon body was not extracted from the source skin: " + name);
            foreach (var renderer in model.GetComponentsInChildren<MeshRenderer>(true))
                if (renderer.sharedMaterials.Any(m => m != null && (m.name.ToLowerInvariant().Contains("arms") || m.name.ToLowerInvariant().Contains("hands"))))
                    throw new InvalidOperationException("FPS hands material leaked into mechanical weapon: " + name);
            float[] rpm = { 800f, 1000f, 500f }, speed = { 550f, 250f, 800f }, damage = { 25f, 31f, 112f }, range = { 300f, 200f, 700f };
            int[] magazines = { 30, 12, 5 };
            float[] reload = { 1.925f, 2f, 1.56f };
            if (definition.weaponId != weaponIndex || !Mathf.Approximately(definition.roundsPerMinute, rpm[weaponIndex]) ||
                !Mathf.Approximately(definition.muzzleVelocity, speed[weaponIndex]) || !Mathf.Approximately(definition.damage, damage[weaponIndex]) ||
                !Mathf.Approximately(definition.range, range[weaponIndex]) || definition.magazineCapacity != magazines[weaponIndex] ||
                !Mathf.Approximately(definition.reloadDuration, reload[weaponIndex]) || !Mathf.Approximately(definition.aimDuration, 0.2f) ||
                !Mathf.Approximately(definition.headDamageMultiplier, 2.5f))
                throw new InvalidOperationException("Definition differs from verified Demo values: " + name);
            byte[] expectedSights = weaponIndex == 1 ? new byte[] { 0, 3 } : new byte[] { 0, 1, 2, 3, 4, 5 };
            if (!definition.sights.Select(s => s.sightId).SequenceEqual(expectedSights) || !model.sights.Select(s => s.sightId).SequenceEqual(expectedSights))
                throw new InvalidOperationException("Incorrect compatible sights: " + name);
            foreach (var sight in model.sights)
            {
                if (sight.aimPoint == null || sight.visual == null || (sight.sightId != 0 && sight.visual.GetComponentsInChildren<MeshRenderer>(true).Length == 0))
                    throw new InvalidOperationException("Sight geometry or aim contact is missing: " + name + "/" + sight.sightId);
                if (sight.eyeRelief < 0.12f)
                    throw new InvalidOperationException("Missing shared-camera eye relief: " + name + "/" + sight.sightId);
                if (sight.sightId != 0)
                {
                    var renderers = sight.visual.GetComponentsInChildren<MeshRenderer>(true);
                    float nearest = renderers.Min(r => r.bounds.min.z) - (sight.aimPoint.position.z - sight.eyeRelief);
                    if (nearest < 0.0649f) throw new InvalidOperationException("Optic intersects camera clearance: " + name + "/" + sight.sightId);
                    if (sight.sightId == 2 && renderers.Where(r => r.name.Contains("Red Dot 2 Glass")).Any(r => r.sharedMaterials.Any(m => m == null || m.name != "PvpOpticGlass")))
                        throw new InvalidOperationException("Opaque gun material remains on optic glass: " + name);
                    if (weaponIndex == 2 && sight.sightId == 1)
                    {
                        var housing = renderers.Single(r => r.name.EndsWith("_Red Dot Sight", StringComparison.Ordinal));
                        Vector3 aperture = RedDotOneApertureCenter(housing);
                        Vector3 point = sight.aimPoint.position;
                        if (Vector2.Distance(new Vector2(aperture.x, aperture.y), new Vector2(point.x, point.y)) > 0.00025f)
                            throw new InvalidOperationException("Sniper Red Dot 1 aim point misses the physical aperture.");
                        var lens = renderers.Single(r => r.sharedMaterials.Any(m => m != null && m.name == "PvpRedDotReticle"));
                        if (Vector3.Distance(lens.bounds.center, point) > 0.00025f)
                            throw new InvalidOperationException("Sniper Red Dot 1 animated lens diverges from its aim point.");
                    }
                }
            }
            if ((model.sights[0].aimPoint.localPosition - new Vector3(0f, 0.09f, 0.05f)).sqrMagnitude < 0.000001f)
                throw new InvalidOperationException("Placeholder iron aim point remains in " + name);
            if (weaponIndex == 2)
            {
                var expectedEdge = SniperReceiverNearEyePoints(model.transform, model.sights[0].aimPoint.localPosition);
                var actualEdge = model.sights[0].nearEyePoints;
                if (actualEdge == null || actualEdge.Length != expectedEdge.Length ||
                    expectedEdge.Any(p => !actualEdge.Any(a => (p - a).sqrMagnitude < 0.00000001f)))
                    throw new InvalidOperationException("Sniper near-eye points do not match its actual rear receiver mesh.");
            }
            foreach (var clip in new[] { model.fireAnimation, model.reloadAnimation, model.equipAnimation, model.idleAnimation })
            {
                if (clip == null || AnimationUtility.GetAnimationEvents(clip).Length != 0)
                    throw new InvalidOperationException("Invalid baked mechanical clip: " + name);
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                    if (binding.path.Contains("Rig/") || binding.path.Contains("hand_L") || binding.path.Contains("upperarm"))
                        throw new InvalidOperationException("Hand skeleton animation leaked into " + name);
            }
            foreach (var component in definition.modelPrefab.GetComponentsInChildren<MonoBehaviour>(true))
                if (component == null || !(component is PvpWeaponModel)) throw new InvalidOperationException("Unexpected framework behaviour in " + name);
            var mechanical = model.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.name.StartsWith("Mechanical_", StringComparison.Ordinal)).ToArray();
            Bounds bounds = mechanical[0].bounds;
            foreach (var renderer in mechanical) bounds.Encapsulate(renderer.bounds);
            Vector3 size = bounds.size;
            if (float.IsNaN(size.sqrMagnitude) || float.IsInfinity(size.sqrMagnitude) ||
                size.x <= 0.015f || size.y <= 0.05f || size.z <= 0.12f || size.magnitude > 3f || bounds.center.magnitude > 3f)
                throw new InvalidOperationException("Mechanical mesh has invalid world scale: " + name + " size=" + size + " center=" + bounds.center);
            reports.Add(new WeaponReport { name = name, mechanicalRenderers = mechanical.Length,
                opticRenderers = model.GetComponentsInChildren<MeshRenderer>(true).Length - mechanical.Length,
                muzzle = model.muzzle.localPosition, leftGrip = model.leftGrip.localPosition, rightGrip = model.rightGrip.localPosition,
                ironAimPoint = model.sights[0].aimPoint.localPosition,
                mechanicalBoundsSize = bounds.size, roundsPerMinute = definition.roundsPerMinute, shotInterval = definition.ShotInterval,
                damage = definition.damage, range = definition.range, reloadDuration = definition.reloadDuration,
                equipDuration = definition.equipDuration, magazineCapacity = definition.magazineCapacity,
                reserveAmmo = definition.reserveAmmo, sightIds = expectedSights, sightEyeReliefs = model.sights.Select(s => s.eyeRelief).ToArray(),
                ironNearEyePoints = model.sights[0].nearEyePoints });
            weaponIndex++;
        }
        foreach (string name in new[] { "Stone", "Metal", "Wood", "Blood" })
            if (AssetDatabase.LoadAssetAtPath<GameObject>(Output + "/Effects/" + name + "Impact.prefab") == null)
                throw new InvalidOperationException("Missing impact effect: " + name);
        Directory.CreateDirectory("Logs/PvpMigration");
        File.WriteAllText("Logs/PvpMigration/weapon-assets.json", JsonUtility.ToJson(new ValidationReport
            { generatedUtc = DateTime.UtcNow.ToString("O"), weapons = reports.ToArray() }, true));
    }
}
