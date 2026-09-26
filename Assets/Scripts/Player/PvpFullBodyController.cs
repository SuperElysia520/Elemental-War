using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.AI;
using Cinemachine;

/// <summary>One humanoid skeleton for local first person and remote presentation. No extra arms renderer.</summary>
[DefaultExecutionOrder(-90)]
[RequireComponent(typeof(Animator), typeof(CharacterController))]
public sealed class PvpFullBodyController : NetworkBehaviour
{
    public float eyeHeight = 1.6f;
    public float walkSpeed = 5f;
    public float sprintSpeed = 10f;
    [Min(0.01f)] public float movementSmoothTime = 0.13f;
    public float jumpSpeed = 6f;
    public float gravity = -19.62f;
    public float sensitivity = 0.1f;
    public float fieldOfView = 60f;
    public Vector3 hipPosition = new Vector3(0.16f, -0.23f, 0.35f);
    public float aimEyeRelief = 0.3f;
    public Vector3 rightHandRotationOffset;
    public Vector3 leftHandRotationOffset;

    private readonly NetworkVariable<float> pitchState = new NetworkVariable<float>(writePerm: NetworkVariableWritePermission.Owner);
    private readonly NetworkVariable<Vector2> moveState = new NetworkVariable<Vector2>(writePerm: NetworkVariableWritePermission.Owner);
    private readonly NetworkVariable<byte> poseState = new NetworkVariable<byte>(writePerm: NetworkVariableWritePermission.Owner);
    private readonly NetworkVariable<float> yawRecoilState = new NetworkVariable<float>(writePerm: NetworkVariableWritePermission.Owner);
    private MyInputSystem input;
    private Animator animator;
    private CharacterController controller;
    private PvpLoadoutCombat combat;
    private PvpWeaponRig rig;
    private Transform mount;
    private Camera viewCamera;
    private GameObject crosshair;
    private GameObject crosshairPoint;
    private bool controlsEnabled = true;
    private float yaw, pitch, verticalSpeed, ads, remotePitch, remoteYawRecoil, nextPoseSend, cameraFovVelocity;
    private Vector3 velocity, movementSmoothing;
    private Vector2 move, look;
    private Vector2 viewRecoil;
    private Vector3 cameraKick, smoothCameraKick;
    private float cameraSpring, cameraSpringVelocity, springElapsed, cameraRecoilWeight;
    private readonly System.Collections.Generic.List<ShotShake> shotShakes = new System.Collections.Generic.List<ShotShake>(16);
    private struct ShotShake { public float age, seed, amount, roughness, duration; }
    private bool sprinting, aiming, grounded;
    private int animationState = -1;
    private int handsIkLayer;
    private float reloadClipLength = 2f;
    private float equipClipLength = 1f;
    private AudioSource walkingSource, runningSource, movementOneShotSource;
    private PlayerController movementAudioSettings;
    private bool audioWasGrounded;
    private float airborneTime, lowestVerticalSpeed;
    private CinemachineBrain cameraBrain;
    private bool originalBrainEnabled, originalCrosshairActive, ownsLocalView;
    private bool originalCrosshairPointActive;
    private float originalNearClip, originalFov;
    private Vector3 originalCameraPosition;
    private Quaternion originalCameraRotation;
    private static PvpFullBodyController localCameraOwner;
    private static readonly int MoveBlend = Animator.StringToHash("MoveBlend");
    private static readonly int AimingX = Animator.StringToHash("AimingX");
    private static readonly int AimingY = Animator.StringToHash("AimingY");
    private static readonly int ReloadSpeed = Animator.StringToHash("ReloadSpeed");
    private static readonly int EquipSpeed = Animator.StringToHash("EquipSpeed");
    private static readonly int ReloadState = Animator.StringToHash("UpperBody.Reload");
    private static readonly int EquipState = Animator.StringToHash("UpperBody.Equip");
    private static readonly int EmptyState = Animator.StringToHash("UpperBody.Empty");
    private static readonly int[] BodyStates =
    {
        Animator.StringToHash("Base Layer.Idle"), Animator.StringToHash("Base Layer.Move"),
        Animator.StringToHash("Base Layer.Hover"), Animator.StringToHash("Base Layer.Aiming"),
    };
    public bool IsAiming => IsOwner ? aiming : (poseState.Value & 1) != 0;
    public bool IsSprinting => IsOwner ? sprinting : (poseState.Value & 2) != 0;
    public bool IsGrounded => IsOwner ? grounded : (poseState.Value & 4) != 0;
    public Vector3 EyePosition => transform.position + Vector3.up * eyeHeight;
    public Quaternion AimRotation => Quaternion.Euler(
        IsOwner ? pitch - viewRecoil.y : remotePitch,
        transform.eulerAngles.y + (IsOwner ? viewRecoil.x : remoteYawRecoil), 0);
    public float AimFraction => ads;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private bool validationAiming;
    internal void SetValidationAiming(bool value) => validationAiming = value;
#endif

    private void Awake()
    {
        animator = GetComponent<Animator>();
        controller = GetComponent<CharacterController>();
        rig = GetComponent<PvpWeaponRig>();
        combat = GetComponent<PvpLoadoutCombat>();
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        handsIkLayer = animator.GetLayerIndex("HandsIK");
        if (handsIkLayer < 0) handsIkLayer = 0;
        CacheActionClipLengths();
        var agent = GetComponent<NavMeshAgent>(); if (agent != null) agent.enabled = false;
        mount = new GameObject("FullBodyWeaponMount").transform;
        mount.SetParent(transform, false);
    }
    public override void OnNetworkSpawn()
    {
        if (rig == null || combat == null)
        {
            Debug.LogError("[PVP] Full body player requires PvpWeaponRig and PvpLoadoutCombat.", this);
            enabled = false;
            return;
        }
        grounded = controller.enabled && controller.isGrounded;
        var profile = GetComponent<PvpCharacterProfile>();
        PvpLoadoutData data = profile != null ? profile.Loadout.Value : PvpLoadoutData.Default;
        rig.Configure(data.primaryWeaponId, data.primarySightId, data.sidearmSightId, mount);
        rig.SetLocalOwner(IsOwner);
        if (IsOwner)
        {
            input = new MyInputSystem();
            input.Enable();
            yaw = transform.eulerAngles.y;
            ownsLocalView = true;
            localCameraOwner = this;
            viewCamera = Camera.main;
            if (viewCamera != null)
            {
                originalNearClip = viewCamera.nearClipPlane;
                originalFov = viewCamera.fieldOfView;
                originalCameraPosition = viewCamera.transform.position;
                originalCameraRotation = viewCamera.transform.rotation;
                cameraBrain = viewCamera.GetComponent<CinemachineBrain>();
                originalBrainEnabled = cameraBrain != null && cameraBrain.enabled;
                if (cameraBrain != null) cameraBrain.enabled = false;
                viewCamera.nearClipPlane = 0.035f;
                viewCamera.fieldOfView = fieldOfView;
            }
            var old = PlayerController.instance;
            if (old != null) old.UseFullBodyCharacter(GetComponent<PlayerModel>());
            SetupMovementAudio(old);
            crosshair = GameObject.Find("CrossHair");
            originalCrosshairActive = crosshair != null && crosshair.activeSelf;
            crosshairPoint = GameObject.Find("CrossHairPoint");
            originalCrosshairPointActive = crosshairPoint != null && crosshairPoint.activeSelf;
            Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
        }
        if (profile != null) profile.ApplyAppearance();
    }
    public override void OnNetworkDespawn()
    {
        ReleaseLocalControl();
    }
    public override void OnDestroy()
    {
        ReleaseLocalControl();
        base.OnDestroy();
    }
    private void OnDisable()
    {
        StopMovementAudio();
        if (input != null) input.Disable();
    }
    private void OnEnable()
    {
        if (IsSpawned && IsOwner && input != null) input.Enable();
    }
    public void SetControlEnabled(bool value)
    {
        controlsEnabled = value;
        if (!value)
        {
            velocity = movementSmoothing = Vector3.zero;
            move = look = Vector2.zero;
            aiming = sprinting = false;
            combat?.CancelLocalInput();
            CancelActionAnimation();
            ResetMovementAudio();
        }
    }
    public void ResetView(Quaternion rotation)
    {
        yaw = rotation.eulerAngles.y; pitch = 0; verticalSpeed = 0;
        velocity = movementSmoothing = Vector3.zero;
        move = look = Vector2.zero;
        viewRecoil = Vector2.zero; ads = 0;
        cameraKick = smoothCameraKick = Vector3.zero;
        cameraSpring = cameraSpringVelocity = springElapsed = cameraFovVelocity = 0;
        shotShakes.Clear();
        transform.rotation = Quaternion.Euler(0, yaw, 0);
        animationState = -1;
        CancelActionAnimation();
        ResetMovementAudio();
    }
    private void Update()
    {
        if (!IsSpawned || rig == null || combat == null) return;
        float dt = Time.deltaTime;
        if (dt <= 0f) return;
        if (IsOwner && input != null)
        {
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                bool lockMouse = Cursor.lockState != CursorLockMode.Locked;
                Cursor.lockState = lockMouse ? CursorLockMode.Locked : CursorLockMode.None;
                Cursor.visible = !lockMouse;
            }
            bool active = controlsEnabled && Cursor.lockState == CursorLockMode.Locked;
            look = active ? input.Player.Look.ReadValue<Vector2>() : Vector2.zero;
            move = active ? Vector2.ClampMagnitude(input.Player.Move.ReadValue<Vector2>(), 1f) : Vector2.zero;
            PvpSightDefinition currentSight = rig.ActiveDefinition != null
                ? rig.ActiveDefinition.GetSight(rig.ActiveSightId) : null;
            float aimSensitivity = Mathf.Lerp(1f,
                currentSight != null ? currentSight.fieldOfView / Mathf.Max(1f, fieldOfView) : 1f, ads);
            yaw += look.x * sensitivity * aimSensitivity;
            pitch = Mathf.Clamp(pitch - look.y * sensitivity * aimSensitivity, -80, 80);
            transform.rotation = Quaternion.Euler(0, yaw, 0);
            aiming = active && input.Player.IsAiming.IsPressed() && !combat.IsReloading && !combat.IsSwitching;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!controlsEnabled && validationAiming) aiming = !combat.IsReloading && !combat.IsSwitching;
#endif
            bool wantsFire = active && input.Player.Fire.IsPressed();
            sprinting = active && input.Player.IsSprint.IsPressed() && move.y > 0.1f && !aiming && !wantsFire && !combat.IsReloading;
            grounded = controller.isGrounded;
            if (grounded && verticalSpeed < 0) verticalSpeed = -2f;
            bool jumped = active && grounded && controller.enabled && input.Player.IsJumping.WasPressedThisFrame();
            if (jumped)
            {
                verticalSpeed = jumpSpeed;
                if (movementAudioSettings != null)
                    PlayMovementOneShot(movementAudioSettings.jumpSound, movementAudioSettings.jumpVolume, 1.02f);
            }
            verticalSpeed += gravity * dt;
            var definition = rig.ActiveDefinition;
            float speed = sprinting ? sprintSpeed : walkSpeed;
            if (definition != null) speed *= aiming ? definition.aimMovementMultiplier : (wantsFire ? definition.fireMovementMultiplier : 1f);
            Vector3 target = (transform.right * move.x + transform.forward * move.y) * speed;
            velocity = Vector3.SmoothDamp(velocity, target, ref movementSmoothing,
                movementSmoothTime, Mathf.Infinity, dt);
            if (controlsEnabled && controller.enabled)
            {
                CollisionFlags collisions = controller.Move((velocity + Vector3.up * verticalSpeed) * dt);
                if ((collisions & CollisionFlags.Above) != 0 && verticalSpeed > 0f)
                    verticalSpeed = 0f;
                grounded = controller.isGrounded && !jumped;
                UpdateMovementAudio(dt);
                if (grounded && verticalSpeed < 0f) verticalSpeed = -2f;
            }
            else ResetMovementAudio();
            if (active)
            {
                if (input.Player.First.WasPressedThisFrame()) combat.RequestSlot(0);
                if (input.Player.Second.WasPressedThisFrame()) combat.RequestSlot(1);
                if (Mouse.current != null && Mathf.Abs(Mouse.current.scroll.ReadValue().y) > 0.1f) combat.RequestSlot((byte)(combat.ActiveSlot == 0 ? 1 : 0));
                if (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame) combat.RequestReload();
                if (Keyboard.current != null && Keyboard.current.vKey.wasPressedThisFrame) combat.RequestFireMode();
                combat.SetTrigger(wantsFire, input.Player.Fire.WasPressedThisFrame(), aiming, AimRotation * Vector3.forward);
            }
            else combat.CancelLocalInput();
            if (Time.unscaledTime >= nextPoseSend)
            {
                pitchState.Value = pitch - viewRecoil.y;
                yawRecoilState.Value = viewRecoil.x;
                moveState.Value = move;
                poseState.Value = (byte)((aiming ? 1 : 0) | (sprinting ? 2 : 0) | (grounded ? 4 : 0));
                nextPoseSend = Time.unscaledTime + 0.05f;
            }
        }
        else
        {
            remotePitch = Mathf.LerpAngle(remotePitch, pitchState.Value, 1 - Mathf.Exp(-20 * dt));
            remoteYawRecoil = Mathf.LerpAngle(remoteYawRecoil, yawRecoilState.Value, 1 - Mathf.Exp(-20 * dt));
            move = moveState.Value;
            aiming = IsAiming; sprinting = IsSprinting; grounded = IsGrounded; look = Vector2.zero;
        }
        ads = Mathf.MoveTowards(ads, aiming ? 1 : 0, dt / Mathf.Max(0.04f, rig.ActiveDefinition != null ? rig.ActiveDefinition.aimDuration : 0.2f));
        cameraKick = Vector3.Lerp(cameraKick, Vector3.zero, 35f * dt);
        smoothCameraKick = Vector3.Slerp(smoothCameraKick, cameraKick, 10f * dt);
        cameraSpring = Mathf.SmoothDamp(cameraSpring, 0, ref cameraSpringVelocity, 0.5f, Mathf.Infinity, dt);
        springElapsed += dt;
        Vector3 kick = (smoothCameraKick + new Vector3(-2f, 0, 0) * (Mathf.Sin(springElapsed * 15f) * cameraSpring * cameraSpring)) * cameraRecoilWeight;
        viewRecoil = new Vector2(kick.y, -kick.x);
        AnimateBody(dt);
        rig.TickPresentation(dt, ads, look * sensitivity, move.magnitude * (sprinting ? sprintSpeed : walkSpeed), sprinting, grounded);
        if (crosshair != null) crosshair.SetActive(ads < 0.7f && originalCrosshairActive);
        if (crosshairPoint != null) crosshairPoint.SetActive(ads < 0.7f && originalCrosshairPointActive);
    }
    private void AnimateBody(float dt)
    {
        int state = !grounded ? 2 : aiming ? 3 : move.sqrMagnitude > 0.001f ? 1 : 0;
        if (state != animationState)
        {
            animator.CrossFadeInFixedTime(BodyStates[state], 0.15f, 0);
            animationState = state;
        }
        animator.SetFloat(MoveBlend, sprinting ? 1f : 0f, 0.1f, dt);
        animator.SetFloat(AimingX, move.x, 0.1f, dt);
        animator.SetFloat(AimingY, move.y, 0.1f, dt);
        if (animator.layerCount > 1)
        {
            float targetWeight = controlsEnabled && (combat.IsReloading || combat.IsSwitching) ? 1f : 0f;
            animator.SetLayerWeight(1, Mathf.MoveTowards(animator.GetLayerWeight(1), targetWeight, dt * 10f));
        }
    }
    public void PlayReloadAnimation(float duration, float elapsed = 0f)
    {
        if (animator.layerCount < 2) return;
        animator.SetLayerWeight(1, 1f);
        animator.SetFloat(ReloadSpeed, reloadClipLength / Mathf.Max(0.1f, duration));
        float clipOffset = Mathf.Clamp01(elapsed / Mathf.Max(0.1f, duration)) * reloadClipLength;
        animator.CrossFadeInFixedTime(ReloadState, Mathf.Min(0.1f, duration * 0.1f), 1, clipOffset);
    }
    public void PlayEquipAnimation(float duration)
    {
        if (animator.layerCount < 2) return;
        animator.SetLayerWeight(1, 1f);
        animator.SetFloat(EquipSpeed, equipClipLength / Mathf.Max(0.1f, duration));
        animator.CrossFadeInFixedTime(EquipState, Mathf.Min(0.1f, duration * 0.1f), 1, 0f);
    }
    public void CancelActionAnimation()
    {
        if (animator == null || animator.layerCount < 2) return;
        animator.SetLayerWeight(1, 0f);
        animator.Play(EmptyState, 1, 0f);
    }
    public void AddShotRecoil(PvpWeaponDefinition definition)
    {
        if (!IsOwner || definition == null) return;
        // The reference feeds these named fields into look X/Y respectively; keep its actual behavior.
        yaw += definition.verticalRecoil * 1.005f;
        pitch = Mathf.Clamp(pitch - definition.horizontalRecoil * 1.005f, -80f, 80f);
        cameraKick += new Vector3(-3f, 0, 1f + Random.Range(-5f, 5f));
        cameraRecoilWeight = definition.cameraRecoil;
        cameraSpring = 0.35f; cameraSpringVelocity = springElapsed = 0;
        if (shotShakes.Count == 32) shotShakes.RemoveAt(0);
        shotShakes.Add(new ShotShake { seed = Random.Range(0f, 100f), amount = definition.cameraShakeAmount,
            roughness = definition.cameraShakeRoughness, duration = definition.cameraShakeDuration });
    }
    private void OnAnimatorIK(int layerIndex)
    {
        if (!IsSpawned || layerIndex != handsIkLayer || rig == null || rig.RightGrip == null || !animator.isHuman) return;
        Quaternion rotation = AimRotation;
        mount.SetPositionAndRotation(EyePosition + rotation * hipPosition, rotation);
        if (rig.Sight != null && ads > 0)
        {
            Vector3 desiredSight = EyePosition + rotation * new Vector3(0, 0, Mathf.Max(aimEyeRelief, rig.SightEyeRelief));
            // Align the neutral sight. Aligning the animated sight here would cancel ADS kick and sway.
            var model = rig.ActiveModel;
            Vector3 sightInModel = model.transform.InverseTransformPoint(rig.Sight.position);
            Vector3 neutralSight = mount.TransformPoint(model.neutralPosition + Quaternion.Euler(model.neutralEuler) * sightInModel);
            mount.position += (desiredSight - neutralSight) * ads;
        }
        rig.ConstrainAimPresentation(EyePosition, rotation, 0.035f);
        // The gun drives both hand targets on this same skeleton, including the reload hand path.
        animator.SetIKPositionWeight(AvatarIKGoal.RightHand, 1);
        animator.SetIKRotationWeight(AvatarIKGoal.RightHand, 1);
        animator.SetIKPosition(AvatarIKGoal.RightHand, rig.RightGrip.position);
        animator.SetIKRotation(AvatarIKGoal.RightHand, rig.RightGrip.rotation * Quaternion.Euler(rightHandRotationOffset));
        if (rig.LeftGrip != null)
        {
            animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, 1);
            animator.SetIKRotationWeight(AvatarIKGoal.LeftHand, 1);
            animator.SetIKPosition(AvatarIKGoal.LeftHand, rig.LeftGrip.position);
            animator.SetIKRotation(AvatarIKGoal.LeftHand, rig.LeftGrip.rotation * Quaternion.Euler(leftHandRotationOffset));
        }
        else
        {
            animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, 0);
            animator.SetIKRotationWeight(AvatarIKGoal.LeftHand, 0);
        }
        animator.SetLookAtWeight(0.5f, 0.35f, 0.7f, 0, 0.6f);
        animator.SetLookAtPosition(EyePosition + rotation * Vector3.forward * 10);
    }
    private void LateUpdate()
    {
        if (!IsOwner || viewCamera == null || !IsSpawned || rig == null) return;
        Vector3 shake = Vector3.zero;
        for (int i = shotShakes.Count - 1; i >= 0; i--)
        {
            // The reference samples a shake once for position and once for rotation each frame.
            var entry = shotShakes[i]; entry.age += Time.deltaTime * 2f;
            if (entry.age >= entry.duration + 0.1f) { shotShakes.RemoveAt(i); continue; }
            float fade = entry.age < entry.duration ? entry.age / Mathf.Max(0.001f, entry.duration) : 1f - (entry.age - entry.duration) / 0.1f;
            entry.seed += Time.deltaTime * 2f * entry.roughness * 5f * (entry.age < entry.duration ? 1f : fade);
            float tick = entry.seed;
            shake += new Vector3(Mathf.PerlinNoise(tick, 0) - 0.5f, Mathf.PerlinNoise(0, tick) - 0.5f,
                Mathf.PerlinNoise(tick, tick) - 0.5f) * (entry.amount * fade * 5f);
            shotShakes[i] = entry;
        }
        shake.z += smoothCameraKick.z * cameraRecoilWeight;
        viewCamera.transform.SetPositionAndRotation(EyePosition, AimRotation * Quaternion.Euler(shake));
        var sight = rig.ActiveDefinition != null ? rig.ActiveDefinition.GetSight(rig.ActiveSightId) : null;
        float target = Mathf.Lerp(fieldOfView, sight != null ? sight.fieldOfView : 55f, ads);
        viewCamera.fieldOfView = Mathf.SmoothDamp(viewCamera.fieldOfView, target, ref cameraFovVelocity, 0.12f);
    }

    private void CacheActionClipLengths()
    {
        if (animator.runtimeAnimatorController == null) return;
        foreach (AnimationClip clip in animator.runtimeAnimatorController.animationClips)
        {
            if (clip.name == "Reload") reloadClipLength = clip.length;
            else if (clip.name == "Equip") equipClipLength = clip.length;
        }
    }

    private void SetupMovementAudio(PlayerController settings)
    {
        movementAudioSettings = settings;
        if (settings == null) return;
        if (walkingSource == null) walkingSource = CreateMovementAudioSource(true);
        if (runningSource == null) runningSource = CreateMovementAudioSource(true);
        if (movementOneShotSource == null) movementOneShotSource = CreateMovementAudioSource(false);
        ResetMovementAudio();
    }

    private AudioSource CreateMovementAudioSource(bool loop)
    {
        AudioSource source = gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = loop;
        source.spatialBlend = 0f;
        source.dopplerLevel = 0f;
        source.volume = 0f;
        return source;
    }

    private void UpdateMovementAudio(float dt)
    {
        if (movementAudioSettings == null) return;
        Vector3 horizontal = controller.velocity;
        horizontal.y = 0f;
        bool movingOnGround = grounded && horizontal.sqrMagnitude > 0.04f;
        UpdateMovementLoop(walkingSource, movementAudioSettings.walkingLoopSound,
            movingOnGround && !sprinting ? movementAudioSettings.walkingVolume : 0f, 1f, dt);
        UpdateMovementLoop(runningSource, movementAudioSettings.runningLoopSound,
            movingOnGround && sprinting ? movementAudioSettings.runningVolume : 0f, 1.03f, dt);

        if (!grounded)
        {
            if (audioWasGrounded) airborneTime = lowestVerticalSpeed = 0f;
            airborneTime += dt;
            lowestVerticalSpeed = Mathf.Min(lowestVerticalSpeed, verticalSpeed);
        }
        else if (!audioWasGrounded)
        {
            if (airborneTime >= movementAudioSettings.minimumLandingAirTime)
            {
                float impact = Mathf.Lerp(0.7f, 1f, Mathf.InverseLerp(2f, 10f, -lowestVerticalSpeed));
                PlayMovementOneShot(movementAudioSettings.landingSound,
                    movementAudioSettings.landingVolume * impact, Random.Range(0.96f, 1.02f));
            }
            airborneTime = lowestVerticalSpeed = 0f;
        }
        audioWasGrounded = grounded;
    }

    private void UpdateMovementLoop(AudioSource source, AudioClip clip, float volume, float pitchMultiplier, float dt)
    {
        if (source == null) return;
        if (clip == null) volume = 0f;
        else if (source.clip != clip)
        {
            source.Stop();
            source.clip = clip;
            source.volume = 0f;
        }
        source.pitch = pitchMultiplier;
        source.volume = Mathf.MoveTowards(source.volume, volume, movementAudioSettings.movementLoopFadeSpeed * dt);
        if (volume > 0f && clip != null && !source.isPlaying) source.Play();
        else if (volume <= 0f && source.volume <= 0.0001f && source.isPlaying) source.Stop();
    }

    private void PlayMovementOneShot(AudioClip clip, float volume, float pitchMultiplier)
    {
        if (clip == null || movementOneShotSource == null || volume <= 0f) return;
        movementOneShotSource.volume = 1f;
        movementOneShotSource.pitch = pitchMultiplier;
        movementOneShotSource.PlayOneShot(clip, Mathf.Clamp01(volume));
    }

    private void ResetMovementAudio()
    {
        StopMovementAudio();
        audioWasGrounded = controller != null && controller.enabled && controller.isGrounded;
        airborneTime = lowestVerticalSpeed = 0f;
    }

    private void StopMovementAudio()
    {
        if (walkingSource != null) { walkingSource.Stop(); walkingSource.volume = 0f; }
        if (runningSource != null) { runningSource.Stop(); runningSource.volume = 0f; }
        if (movementOneShotSource != null) movementOneShotSource.Stop();
    }

    private void ReleaseLocalControl()
    {
        StopMovementAudio();
        if (input != null) { input.Disable(); input.Dispose(); input = null; }
        if (!ownsLocalView) return;
        ownsLocalView = false;
        // 被新实例接管的相机不能再由已销毁的旧玩家恢复。
        if (localCameraOwner != this) return;
        localCameraOwner = null;
        if (viewCamera != null)
        {
            viewCamera.nearClipPlane = originalNearClip;
            viewCamera.fieldOfView = originalFov;
            viewCamera.transform.SetPositionAndRotation(originalCameraPosition, originalCameraRotation);
        }
        if (cameraBrain != null) cameraBrain.enabled = originalBrainEnabled;
        if (crosshair != null) crosshair.SetActive(originalCrosshairActive);
        if (crosshairPoint != null) crosshairPoint.SetActive(originalCrosshairPointActive);
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }
}
