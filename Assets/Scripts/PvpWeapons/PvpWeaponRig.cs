using UnityEngine;

/// <summary>Two mechanical weapons on the shared full-body rig. No input, damage, or character ownership.</summary>
public sealed class PvpWeaponRig : MonoBehaviour
{
    private readonly PvpWeaponModel[] models = new PvpWeaponModel[2];
    private readonly PvpWeaponDefinition[] definitions = new PvpWeaponDefinition[2];
    private readonly byte[] sightIds = new byte[2];
    private Transform mount;
    private byte slot;
    private Transform selectedSight;
    private Vector3 kickPosition, kickRotation, smoothKickPosition, smoothKickRotation;
    private Vector3 swayPosition, swayRotation;
    private float ads, elapsed, walkingPhase, springTime, springPositionProgress, springRotationProgress;
    private float walkingSpeed, walkingAmount, sprintProgress;
    private float springPositionVelocity, springRotationVelocity;
    private float reloadTime, reloadLength;
    private bool reloading, visible = true, localAudio;
    private PvpAudioCue[] activeCues;
    private float cueTime;
    private int nextCue;
    private AudioSource shotAudio, actionAudio;
    private GameObject[] casings;
    private Rigidbody[] casingBodies;
    private float[] casingExpiry;
    private int nextCasing;
    private PvpWeaponModel clearanceModel;
    private byte clearanceSight;
    private MeshRenderer[] clearanceRenderers;
    private Vector3[] nearEyePoints;

    public PvpWeaponDefinition ActiveDefinition => definitions[slot];
    public byte ActiveWeaponId => ActiveDefinition != null ? ActiveDefinition.weaponId : (byte)0;
    public byte ActiveSightId => sightIds[slot];
    public byte ActiveSlot => slot;
    public PvpWeaponModel ActiveModel => models[slot];
    public Transform Muzzle => ActiveModel != null ? ActiveModel.muzzle : null;
    public Transform RightGrip => ActiveModel != null ? ActiveModel.rightGrip : null;
    public Transform LeftGrip => ActiveModel != null ? (reloading && ActiveModel.reloadHandTarget != null ? ActiveModel.reloadHandTarget : ActiveModel.leftGrip) : null;
    public Transform ReloadHandTarget => ActiveModel != null ? ActiveModel.reloadHandTarget : null;
    public Transform Sight => selectedSight;
    public float SightEyeRelief
    {
        get
        {
            if (ActiveModel != null && ActiveModel.sights != null)
                foreach (var sight in ActiveModel.sights)
                    if (sight.sightId == ActiveSightId) return Mathf.Max(0.12f, sight.eyeRelief);
            return 0.12f;
        }
    }
    public bool IsReloading => reloading;
    public float ReloadProgress => reloading ? Mathf.Clamp01(reloadTime / Mathf.Max(0.01f, reloadLength)) : 0f;
    public Vector3 LocalPositionOffset { get; private set; }
    public Quaternion LocalRotationOffset { get; private set; } = Quaternion.identity;
    public float LastAimClearanceDepth { get; private set; } = float.PositiveInfinity;
    public float LastAimClearanceCorrection { get; private set; }

    /// <summary>Call after the mount is aligned to the eye, before solving the shared hands.</summary>
    public void ConstrainAimPresentation(Vector3 eyePosition, Quaternion aimRotation, float nearClipPlane)
    {
        LastAimClearanceDepth = float.PositiveInfinity;
        LastAimClearanceCorrection = 0f;
        if (ActiveModel == null || ads <= 0.5f) return;
        if (clearanceModel != ActiveModel || clearanceSight != ActiveSightId || clearanceRenderers == null)
        {
            clearanceModel = ActiveModel;
            clearanceSight = ActiveSightId;
            clearanceRenderers = new MeshRenderer[0];
            nearEyePoints = null;
            if (ActiveModel.sights != null)
                foreach (var sight in ActiveModel.sights)
                    if (sight.sightId == ActiveSightId && sight.visual != null)
                    {
                        clearanceRenderers = sight.visual.GetComponentsInChildren<MeshRenderer>(true);
                        nearEyePoints = sight.nearEyePoints;
                        break;
                    }
        }
        Vector3 forward = aimRotation * Vector3.forward;
        float nearest = float.PositiveInfinity;
        // Bare sniper/pistol sights are authored receiver-axis points, not separate
        // meshes. Protect that near-eye plane as well; including the whole receiver
        // bounds would also count the stock behind the eye and push the gun too far.
        if (ActiveSightId == 0 && clearanceRenderers.Length == 0 && selectedSight != null)
            nearest = Vector3.Dot(selectedSight.position - eyePosition, forward);
        if (nearEyePoints != null)
            foreach (Vector3 point in nearEyePoints)
                nearest = Mathf.Min(nearest, Vector3.Dot(ActiveModel.transform.TransformPoint(point) - eyePosition, forward));
        foreach (var renderer in clearanceRenderers)
        {
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
            // Use each mesh's own bounds transformed by its current mechanical pose.
            // World AABBs would overestimate the required shift as the player turns.
            Bounds bounds = renderer.localBounds;
            Vector3 min = bounds.min, max = bounds.max;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = new Vector3((i & 1) == 0 ? min.x : max.x,
                    (i & 2) == 0 ? min.y : max.y, (i & 4) == 0 ? min.z : max.z);
                float depth = Vector3.Dot(renderer.transform.TransformPoint(corner) - eyePosition, forward);
                nearest = Mathf.Min(nearest, depth);
            }
        }
        if (float.IsPositiveInfinity(nearest)) return;
        // The reference renders its viewmodel separately. On the shared world camera,
        // keep its rear surface ahead of the near plane while retaining all angular kick.
        // Blend in during ADS; at full aim this is a strict geometric lower bound.
        float correction = Mathf.Max(0f, Mathf.Max(0.001f, nearClipPlane) + 0.01f - nearest) * Mathf.SmoothStep(0f, 1f, (ads - 0.5f) * 2f);
        if (correction > 0f)
        {
            Vector3 shift = forward * correction;
            ActiveModel.transform.position += shift;
            LocalPositionOffset += ActiveModel.transform.parent.InverseTransformVector(shift);
        }
        LastAimClearanceDepth = nearest + correction;
        LastAimClearanceCorrection = correction;
    }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    internal bool SetValidationSight(byte id)
    {
        if (ActiveDefinition == null || ActiveDefinition.GetSight(id)?.sightId != id || ActiveModel == null) return false;
        sightIds[slot] = id;
        selectedSight = ActiveModel.SelectSight(id);
        return selectedSight != null;
    }
#endif

    public void Configure(byte primaryWeaponId, byte primarySightId, byte sidearmSightId, Transform weaponMount)
    {
        ClearModels();
        mount = weaponMount != null ? weaponMount : transform;
        definitions[0] = PvpWeaponCatalog.Get(primaryWeaponId == 2 ? (byte)2 : (byte)0);
        definitions[1] = PvpWeaponCatalog.Get(1);
        sightIds[0] = primarySightId;
        sightIds[1] = sidearmSightId == 3 ? (byte)3 : (byte)0;
        for (int i = 0; i < models.Length; i++)
        {
            if (definitions[i] == null || definitions[i].modelPrefab == null) continue;
            var instance = Instantiate(definitions[i].modelPrefab, mount, false);
            models[i] = instance.GetComponent<PvpWeaponModel>();
            var sight = definitions[i].GetSight(sightIds[i]);
            sightIds[i] = sight != null ? sight.sightId : (byte)0;
            if (models[i] != null) models[i].SelectSight(sightIds[i]);
        }
        EnsureAudio();
        CreateCasingPool();
        slot = 0;
        SelectSlot(0);
    }

    public void SelectSlot(byte value)
    {
        CancelReload();
        slot = value == 1 ? (byte)1 : (byte)0;
        kickPosition = kickRotation = smoothKickPosition = smoothKickRotation = Vector3.zero;
        swayPosition = swayRotation = Vector3.zero;
        springPositionProgress = springRotationProgress = 0f;
        walkingSpeed = walkingAmount = sprintProgress = 0f;
        for (int i = 0; i < models.Length; i++) if (models[i] != null) models[i].gameObject.SetActive(visible && i == slot);
        selectedSight = ActiveModel != null ? ActiveModel.SelectSight(sightIds[slot]) : null;
        if (ActiveModel != null) PlayMechanical(ActiveModel.equipAnimation, ActiveDefinition.equipDuration);
        StartCues(ActiveDefinition != null ? ActiveDefinition.equipCues : null);
    }

    public void SetVisible(bool value)
    {
        visible = value;
        for (int i = 0; i < models.Length; i++) if (models[i] != null) models[i].gameObject.SetActive(value && i == slot);
    }

    public void SetLocalOwner(bool value)
    {
        localAudio = value;
        EnsureAudio();
        shotAudio.spatialBlend = actionAudio.spatialBlend = value ? 0f : 1f;
    }

    private void FixedUpdate()
    {
        if (ActiveModel == null || ActiveDefinition == null) return;
        var k = ads > 0.5f ? ActiveDefinition.aimKick : ActiveDefinition.hipKick;
        float dt = Time.fixedDeltaTime;
        // Both Demo weapon kick modifiers explicitly use FixedUpdate for decay and smoothing.
        kickPosition = Vector3.Lerp(kickPosition, Vector3.zero, 35f * dt);
        kickRotation = Vector3.Lerp(kickRotation, Vector3.zero, 35f * dt);
        smoothKickPosition = Vector3.Slerp(smoothKickPosition, kickPosition, k.positionRoughness * dt);
        smoothKickRotation = Vector3.Slerp(smoothKickRotation, kickRotation, k.rotationRoughness * dt);
    }

    public void TickPresentation(float dt, float aimProgress, Vector2 lookDelta, float moveSpeed, bool sprint, bool grounded)
    {
        if (ActiveModel == null || ActiveDefinition == null || dt <= 0f) return;
        dt = Mathf.Min(dt, 0.1f);
        ads = Mathf.Clamp01(aimProgress);
        elapsed += dt;
        var d = ActiveDefinition;
        var k = ads > 0.5f ? d.aimKick : d.hipKick;
        var motion = d.motion;
        float springSpeed = Mathf.Max(0.001f, k.springSpeedMultiplier);
        springTime += dt * springSpeed;
        float oldPositionProgress = springPositionProgress, oldRotationProgress = springRotationProgress;
        springPositionProgress = Mathf.SmoothDamp(springPositionProgress, 0f, ref springPositionVelocity, k.springPositionFade / springSpeed, Mathf.Infinity, dt);
        springRotationProgress = Mathf.SmoothDamp(springRotationProgress, 0f, ref springRotationVelocity, k.springRotationFade / springSpeed, Mathf.Infinity, dt);
        Vector3 springPos = k.springPosition * (Mathf.Sin(springTime * k.springPositionSpeed) * oldPositionProgress * springPositionProgress);
        Vector3 springRot = k.springRotation * (Mathf.Sin(springTime * k.springRotationSpeed) * oldRotationProgress * springRotationProgress);
        Vector2 input = lookDelta / Mathf.Max(dt, 0.001f) * 0.5f;
        Vector3 targetSwayPosition = ClampAxes(new Vector3(input.x, input.y, 0f) * motion.swayPositionInput, motion.swayPositionLimit) * motion.swayAmplification;
        Vector3 targetSwayRotation = ClampAxes(new Vector3(-input.y, input.x, 0f) * motion.swayRotationInput, motion.swayRotationLimit) * motion.swayAmplification;
        swayPosition = Vector3.Lerp(swayPosition, targetSwayPosition, motion.swaySmoothness * dt);
        swayRotation = Vector3.Lerp(swayRotation, targetSwayRotation, motion.swaySmoothness * dt);
        float hip = 1f - ads;
        Vector3 breath = Wave(motion.aimBreathPosition, motion.aimBreathPositionFrequency, elapsed) + Mathf.Sin(elapsed) * motion.hipBreathPosition * hip;
        Vector3 breathRot = Mathf.Sin(elapsed) * (motion.aimBreathRotation + motion.hipBreathRotation * hip);
        walkingSpeed = Mathf.Lerp(walkingSpeed, moveSpeed, dt * 5f);
        walkingAmount = Mathf.Lerp(walkingAmount, grounded && moveSpeed > 1f ? moveSpeed : 0f, dt * 5f);
        walkingPhase += dt * walkingSpeed;
        Vector3 bob = Wave(motion.walkPosition, motion.walkPositionFrequency, walkingPhase) * (walkingAmount * hip);
        Vector3 bobRot = Wave(motion.walkRotation, motion.walkRotationFrequency, walkingPhase) * (walkingAmount * hip);
        LocalPositionOffset = smoothKickPosition + springPos + swayPosition + breath + bob;
        LocalRotationOffset = Quaternion.Euler(smoothKickRotation + springRot + swayRotation + breathRot + bobRot);
        sprintProgress = Mathf.MoveTowards(sprintProgress, sprint && !reloading ? 1f : 0f, dt / Mathf.Max(0.01f, motion.sprintDuration));
        LocalPositionOffset += motion.sprintPosition * (sprintProgress * hip);
        LocalRotationOffset *= Quaternion.Euler(motion.sprintRotation * (sprintProgress * hip));
        ActiveModel.transform.localPosition = ActiveModel.neutralPosition + LocalPositionOffset;
        ActiveModel.transform.localRotation = Quaternion.Euler(ActiveModel.neutralEuler) * LocalRotationOffset;
        if (reloading)
        {
            reloadTime += dt;
            if (reloadTime >= reloadLength) CancelReload();
        }
        TickCues(dt);
        if (casings != null) for (int i = 0; i < casings.Length; i++)
            if (casings[i] != null && casings[i].activeSelf && Time.time >= casingExpiry[i]) casings[i].SetActive(false);
    }

    public void PlayShot(bool localOwner)
    {
        if (ActiveModel == null || ActiveDefinition == null) return;
        SetLocalOwner(localOwner);
        var d = ActiveDefinition;
        var hip = d.hipKick;
        var aim = d.aimKick;
        kickPosition += Vector3.Lerp(RandomPosition(hip), RandomPosition(aim), ads);
        kickRotation += Vector3.Lerp(RandomRotation(hip), RandomRotation(aim), ads);
        springTime = 0f;
        springPositionProgress = springRotationProgress = 1f;
        springPositionVelocity = springRotationVelocity = 0f;
        if (ActiveModel.muzzleEffects != null) foreach (var effect in ActiveModel.muzzleEffects)
            if (effect != null && effect.gameObject.activeInHierarchy) effect.Play(true);
        if (d.fireClip != null)
        {
            shotAudio.pitch = Mathf.Max(0.1f, d.firePitch + Random.Range(-d.firePitchRandomness, d.firePitchRandomness));
            shotAudio.maxDistance = d.audioMaxDistance;
            shotAudio.PlayOneShot(d.fireClip, d.fireVolume);
        }
        PlayMechanical(ActiveModel.fireAnimation, 0f);
        if (d.fireCues != null && d.fireCues.Length > 0)
        {
            actionAudio.pitch = d.firePitch;
            StartCues(d.fireCues);
        }
        EjectCasing();
    }

    public void BeginReload(float duration) => BeginReload(duration, false);

    public void BeginReload(float duration, bool empty) => BeginReload(duration, empty, 0f);

    public void BeginReload(float duration, bool empty, float elapsedTime)
    {
        if (ActiveModel == null || ActiveDefinition == null) return;
        reloading = true;
        reloadLength = Mathf.Max(0.1f, duration);
        reloadTime = Mathf.Clamp(elapsedTime, 0f, reloadLength);
        PlayMechanical(ActiveModel.reloadAnimation, reloadLength);
        if (ActiveModel.reloadAnimation != null && ActiveModel.mechanicalAnimation != null)
        {
            var state = ActiveModel.mechanicalAnimation[ActiveModel.reloadAnimation.name];
            if (state != null) state.time = ActiveModel.reloadAnimation.length * (reloadTime / reloadLength);
        }
        actionAudio.pitch = ActiveDefinition.reloadPitch;
        var clip = empty ? ActiveDefinition.emptyReloadClip : ActiveDefinition.reloadClip;
        if (clip != null) actionAudio.PlayOneShot(clip);
        StartCues(empty ? ActiveDefinition.emptyReloadCues : ActiveDefinition.reloadCues);
        cueTime = reloadTime;
        if (activeCues != null)
            while (nextCue < activeCues.Length && activeCues[nextCue].time < cueTime) nextCue++;
    }

    public void CancelReload()
    {
        reloading = false;
        activeCues = null;
        if (actionAudio != null) actionAudio.Stop();
        if (ActiveModel != null && ActiveModel.mechanicalAnimation != null)
        {
            ActiveModel.mechanicalAnimation.Stop();
            if (ActiveModel.idleAnimation != null) ActiveModel.idleAnimation.SampleAnimation(ActiveModel.gameObject, 0f);
        }
    }

    private void PlayMechanical(AnimationClip clip, float duration)
    {
        if (clip == null || ActiveModel == null || ActiveModel.mechanicalAnimation == null) return;
        var animation = ActiveModel.mechanicalAnimation;
        var state = animation[clip.name];
        if (state == null) return;
        state.speed = duration > 0f ? clip.length / duration : 1f;
        state.time = 0f;
        animation.Play(clip.name, PlayMode.StopAll);
    }

    private static Vector3 RandomPosition(PvpKickDefinition k) => k.staticPosition +
        new Vector3(Random.Range(-k.randomPosition.x, k.randomPosition.x), Random.Range(-k.randomPosition.y, k.randomPosition.y), k.randomPosition.z);
    private static Vector3 RandomRotation(PvpKickDefinition k) => k.staticRotation +
        new Vector3(k.randomRotation.x, Random.Range(-k.randomRotation.y, k.randomRotation.y), Random.Range(-k.randomRotation.z, k.randomRotation.z));

    private static Vector3 ClampAxes(Vector3 value, float limit) => new Vector3(
        Mathf.Clamp(value.x, -limit, limit), Mathf.Clamp(value.y, -limit, limit), Mathf.Clamp(value.z, -limit, limit));

    private static Vector3 Wave(Vector3 amount, Vector3 frequency, float time) => new Vector3(
        amount.x * frequency.x * Mathf.Sin(time * frequency.x),
        amount.y * frequency.y * Mathf.Sin(time * frequency.y),
        amount.z * frequency.z * Mathf.Sin(time * frequency.z));

    private void StartCues(PvpAudioCue[] cues) { activeCues = cues; cueTime = 0f; nextCue = 0; }
    private void TickCues(float dt)
    {
        if (activeCues == null) return;
        cueTime += dt;
        while (nextCue < activeCues.Length && activeCues[nextCue].time <= cueTime)
        {
            var cue = activeCues[nextCue++];
            if (cue.clip != null) actionAudio.PlayOneShot(cue.clip);
        }
    }

    private void EnsureAudio()
    {
        if (shotAudio == null) shotAudio = gameObject.AddComponent<AudioSource>();
        if (actionAudio == null) actionAudio = gameObject.AddComponent<AudioSource>();
        SetupAudio(shotAudio);
        SetupAudio(actionAudio);
    }

    private void SetupAudio(AudioSource source)
    {
        source.playOnAwake = false;
        source.spatialBlend = localAudio ? 0f : 1f;
        source.dopplerLevel = 0f;
        source.minDistance = 1f;
        source.maxDistance = 200f;
    }

    private void CreateCasingPool()
    {
        if (casings != null) foreach (var casing in casings) if (casing != null) Destroy(casing);
        casings = new GameObject[24];
        casingBodies = new Rigidbody[24];
        casingExpiry = new float[24];
        for (int i = 0; i < casings.Length; i++)
        {
            var model = models[i / 12];
            if (model == null || model.casingPrefab == null) continue;
            casings[i] = Instantiate(model.casingPrefab);
            casings[i].name = "PVP Casing";
            foreach (var child in casings[i].GetComponentsInChildren<Transform>(true)) child.gameObject.layer = 2;
            casingBodies[i] = casings[i].GetComponent<Rigidbody>();
            if (casingBodies[i] == null) casingBodies[i] = casings[i].AddComponent<Rigidbody>();
            casings[i].SetActive(false);
        }
    }

    private void EjectCasing()
    {
        int index = slot * 12 + nextCasing++ % 12;
        if (casings == null || casings[index] == null || ActiveModel.ejectionPort == null) return;
        var casing = casings[index];
        casing.transform.SetPositionAndRotation(ActiveModel.ejectionPort.position, ActiveModel.ejectionPort.rotation);
        casing.SetActive(true);
        casingBodies[index].velocity = ActiveModel.ejectionPort.right * 5f + Vector3.up;
        casingBodies[index].angularVelocity = Random.insideUnitSphere * 15f;
        casingExpiry[index] = Time.time + 2.5f;
    }

    private void ClearModels()
    {
        for (int i = 0; i < models.Length; i++)
        {
            if (models[i] != null) { models[i].gameObject.SetActive(false); Destroy(models[i].gameObject); }
            models[i] = null;
        }
    }

    private void OnDestroy()
    {
        ClearModels();
        if (casings != null) foreach (var casing in casings) if (casing != null) Destroy(casing);
    }
}
