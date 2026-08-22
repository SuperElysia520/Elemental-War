using UnityEngine;

/// <summary>
/// 第一人称 viewmodel（手臂+枪）：只做本地渲染与开火视觉，不联网。
/// 由 PlayerController 实例化并挂在本地主相机下。
/// 动画参数与 Low Poly FPS Pack 的 Animator Controller 对齐：Aim/Walk/Run 为 Bool，开火用 "Fire" 状态。
/// </summary>
public class FirstPersonViewmodel : MonoBehaviour
{
    public const float PvpHolographicMagnification = 1.5f;

    [Tooltip("枪口（子弹生成点）")] public Transform bulletSpawnPoint;
    [Tooltip("枪口火花粒子")] public ParticleSystem muzzleFlash;
    [Tooltip("开火火花粒子")] public ParticleSystem sparkParticles;

    [Header("PVP 固定全息瞄具")]
    [Min(0.12f)]
    [Tooltip("开镜时全息准心距离相机的距离；越小瞄具在屏幕中越大")]
    public float pvpHolographicEyeRelief = 0.22f;
    [Min(0.01f)]
    [Tooltip("PVP 全息瞄具移动到屏幕中心的平滑时间；越小越快，越大越柔和")]
    public float pvpHolographicAimSmoothTime = 0.1f;
    [Tooltip("PVP 全息镜内显示的准心 Sprite")]
    public Sprite pvpHolographicReticle;
    [Tooltip("进入瞄准时播放一次的音效")]
    public AudioClip aimInSound;
    [Range(0f, 1f)] public float aimInVolume = 1f;

    [Header("PVP 瞄准开火视觉后坐力")]
    [Tooltip("开镜开火时枪身每发向上旋转角度")]
    public float pvpAdsWeaponPitchKick = 0.9f;
    [Tooltip("开镜开火时枪身每发左右旋转角度")]
    public float pvpAdsWeaponYawKick = 0.28f;
    [Tooltip("开镜开火时枪身每发侧倾角度")]
    public float pvpAdsWeaponRollKick = 0.7f;
    [Tooltip("开镜开火时枪身每发向相机方向后坐的距离")]
    public float pvpAdsWeaponBackKick = 0.008f;
    [Tooltip("开镜开火时枪身每发向上跳动的距离")]
    public float pvpAdsWeaponRiseKick = 0f;
    [Tooltip("开镜开火时枪身每发左右位移的距离")]
    public float pvpAdsWeaponSideKick = 0f;
    [Tooltip("枪身后坐力进入速度")]
    public float pvpAdsWeaponKickSpeed = 38f;
    [Tooltip("枪身后坐力回正速度")]
    public float pvpAdsWeaponReturnSpeed = 15f;

    private Animator animator;
    private int aimHash;
    private int aimScope2Hash;
    private int walkHash;
    private int runHash;
    private bool aiming;
    private bool pvpHolographicSightEnabled;
    private Renderer[] viewmodelRenderers;
    private bool[] initialRendererStates;
    private Transform[] viewmodelTransforms;
    private Transform holographicAimPoint;
    private AudioSource aimAudioSource;
    private bool holographicAlignmentActive;
    private Vector3 smoothedHolographicAimPoint;
    private Vector3 holographicAlignmentVelocity;
    private Vector3 lastHolographicAlignmentCorrection;
    private Vector3 pvpWeaponRecoilTarget;
    private Vector3 pvpWeaponRecoilCurrent;
    private Vector3 pvpWeaponPositionTarget;
    private Vector3 pvpWeaponPositionCurrent;
    private Vector3 pvpRenderedAimDirectionLocal = Vector3.forward;
    private bool hasPvpRenderedAimDirection;
    private bool pvpWeaponRecoilPoseApplied;
    private Vector3 pvpWeaponPreRecoilLocalPosition;
    private Quaternion pvpWeaponPreRecoilLocalRotation;

    void Awake()
    {
        animator = GetComponentInChildren<Animator>();
        aimHash = Animator.StringToHash("Aim");
        aimScope2Hash = Animator.StringToHash("Aim Scope 2");
        walkHash = Animator.StringToHash("Walk");
        runHash = Animator.StringToHash("Run");

        // 安全兜底：禁用子节点里残留的相机，避免出现第二个渲染相机
        foreach (var cam in GetComponentsInChildren<Camera>(true))
            cam.enabled = false;

        // 旧生成预制体可能带有 FPS Pack 示例 Canvas，会把教程文字/弹药 UI 一起显示。
        foreach (var canvas in GetComponentsInChildren<Canvas>(true))
            canvas.gameObject.SetActive(false);

        viewmodelRenderers = GetComponentsInChildren<Renderer>(true);
        initialRendererStates = new bool[viewmodelRenderers.Length];
        for (int i = 0; i < viewmodelRenderers.Length; i++)
        {
            Renderer renderer = viewmodelRenderers[i];
            initialRendererStates[i] = renderer != null && renderer.enabled;
            if (renderer == null)
                continue;

            string objectName = renderer.gameObject.name.ToLowerInvariant();
            if (objectName.Contains("knife") || objectName.StartsWith("scope") || objectName == "silencer")
                renderer.enabled = false;
        }
        viewmodelTransforms = GetComponentsInChildren<Transform>(true);
        foreach (Transform child in viewmodelTransforms)
        {
            if (child != null && child.gameObject.name == "Scope 2 Sight Texture")
            {
                holographicAimPoint = child;
                break;
            }
        }

        aimAudioSource = gameObject.AddComponent<AudioSource>();
        aimAudioSource.playOnAwake = false;
        aimAudioSource.loop = false;
        aimAudioSource.spatialBlend = 0f;

        // 禁用 FPS Pack 遗留驱动脚本（AutomaticGunScriptLPFP 等）：它们读旧版
        // Input Manager（新输入系统下恒为 false），每帧强制 SetBool(Aim/Walk/Run)
        // 并 anim.Play("Fire")，与本组件抢同一个 Animator，导致开镜/奔跑/开火动画被打断。
        foreach (var mb in GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb == null || mb == this)
                continue;
            if (mb.GetType().Name.EndsWith("LPFP"))
                mb.enabled = false;
        }
    }

    /// <summary>
    /// PVP 使用资源包自带的 Scope 02 作为固定全息瞄具；PVE 恢复原来的铁瞄。
    /// 该 viewmodel 只存在于本地相机下，因此不会让其他玩家看到瞄具。
    /// </summary>
    public void ConfigurePvpHolographicSight(bool enabled)
    {
        if (!enabled)
            ResetPvpWeaponRecoil();
        pvpHolographicSightEnabled = enabled;
        ApplyAttachmentVisibility();
        SetAiming(aiming);
    }

    public float GetAimFieldOfView(float defaultFieldOfView, float fallbackAimFieldOfView)
    {
        if (!pvpHolographicSightEnabled)
            return fallbackAimFieldOfView;

        float halfFovRadians = defaultFieldOfView * Mathf.Deg2Rad * 0.5f;
        return 2f * Mathf.Atan(Mathf.Tan(halfFovRadians) / PvpHolographicMagnification) * Mathf.Rad2Deg;
    }

    /// <summary>
    /// 当前 viewmodel 是否可以执行 PVP 全息瞄具的动画后对齐。
    /// </summary>
    public bool CanAlignPvpHolographicSight =>
        pvpHolographicSightEnabled && holographicAimPoint != null;

    /// <summary>
    /// 记录本帧最终渲染出来的红点方向。开火发生在下一次 Update，使用这条方向能保证
    /// 玩家看到的红点位置就是服务器收到的射击方向，而不是始终偷偷使用屏幕中心。
    /// </summary>
    public void CapturePvpRenderedAimDirection(Transform cameraTransform, bool shouldAim)
    {
        if (!shouldAim || !CanAlignPvpHolographicSight || cameraTransform == null)
        {
            hasPvpRenderedAimDirection = false;
            return;
        }

        Vector3 worldDirection = holographicAimPoint.position - cameraTransform.position;
        if (worldDirection.sqrMagnitude <= 0.000001f)
        {
            hasPvpRenderedAimDirection = false;
            return;
        }

        pvpRenderedAimDirectionLocal = cameraTransform
            .InverseTransformDirection(worldDirection.normalized)
            .normalized;
        hasPvpRenderedAimDirection = true;
    }

    public bool TryGetPvpRenderedAimDirection(Transform cameraTransform, out Vector3 worldDirection)
    {
        if (cameraTransform != null && hasPvpRenderedAimDirection)
        {
            worldDirection = cameraTransform.TransformDirection(pvpRenderedAimDirectionLocal).normalized;
            return true;
        }

        worldDirection = cameraTransform != null ? cameraTransform.forward : Vector3.forward;
        return false;
    }

    /// <summary>
    /// 移除上一帧 LateUpdate 施加的临时对齐量，让 PlayerController 只平滑基础持枪位置。
    /// 必须在本帧 Animator 计算前调用，避免基础位置与瞄准动画互相追逐造成过冲。
    /// </summary>
    public void ClearHolographicAlignmentCorrection()
    {
        if (lastHolographicAlignmentCorrection.sqrMagnitude <= Mathf.Epsilon)
            return;

        transform.localPosition -= lastHolographicAlignmentCorrection;
        lastHolographicAlignmentCorrection = Vector3.zero;
    }

    /// <summary>
    /// 清除上一帧施加在 viewmodel 根节点上的枪身后坐姿势。
    /// 新的执行顺序是先完成瞄具基础对齐、再施加整枪后坐，因此清理时要先移除后坐姿势。
    /// </summary>
    public void ClearPvpWeaponRecoilPose()
    {
        if (!pvpWeaponRecoilPoseApplied)
            return;

        transform.localPosition = pvpWeaponPreRecoilLocalPosition;
        transform.localRotation = pvpWeaponPreRecoilLocalRotation;
        pvpWeaponRecoilPoseApplied = false;
    }

    /// <summary>更新枪身后坐力的快速进入和较慢回正。</summary>
    public void UpdatePvpWeaponRecoil(float deltaTime)
    {
        float returnT = 1f - Mathf.Exp(-Mathf.Max(0.01f, pvpAdsWeaponReturnSpeed) * deltaTime);
        float kickT = 1f - Mathf.Exp(-Mathf.Max(0.01f, pvpAdsWeaponKickSpeed) * deltaTime);
        pvpWeaponRecoilTarget = Vector3.Lerp(pvpWeaponRecoilTarget, Vector3.zero, returnT);
        pvpWeaponRecoilCurrent = Vector3.Lerp(pvpWeaponRecoilCurrent, pvpWeaponRecoilTarget, kickT);
        pvpWeaponPositionTarget = Vector3.Lerp(pvpWeaponPositionTarget, Vector3.zero, returnT);
        pvpWeaponPositionCurrent = Vector3.Lerp(pvpWeaponPositionCurrent, pvpWeaponPositionTarget, kickT);
    }

    /// <summary>在每发开火时加入与镜头横向后坐同方向的枪身冲击。</summary>
    public void AddPvpAdsShotRecoil(float horizontalDirection, float strength)
    {
        if (!pvpHolographicSightEnabled || !aiming)
            return;

        float safeStrength = Mathf.Max(0.1f, strength);
        float side = Mathf.Abs(horizontalDirection) > 0.001f
            ? Mathf.Sign(horizontalDirection)
            : (Random.value < 0.5f ? -1f : 1f);
        pvpWeaponRecoilTarget.x = Mathf.Clamp(
            pvpWeaponRecoilTarget.x - pvpAdsWeaponPitchKick * safeStrength,
            -pvpAdsWeaponPitchKick * 2.75f,
            0f);
        pvpWeaponRecoilTarget.y = Mathf.Clamp(
            pvpWeaponRecoilTarget.y + side * pvpAdsWeaponYawKick * safeStrength,
            -pvpAdsWeaponYawKick * 2.75f,
            pvpAdsWeaponYawKick * 2.75f);
        pvpWeaponRecoilTarget.z = Mathf.Clamp(
            pvpWeaponRecoilTarget.z - side * pvpAdsWeaponRollKick * safeStrength,
            -pvpAdsWeaponRollKick * 2.75f,
            pvpAdsWeaponRollKick * 2.75f);

        pvpWeaponPositionTarget.x = Mathf.Clamp(
            pvpWeaponPositionTarget.x + side * pvpAdsWeaponSideKick * safeStrength,
            -pvpAdsWeaponSideKick * 2.75f,
            pvpAdsWeaponSideKick * 2.75f);
        pvpWeaponPositionTarget.y = Mathf.Clamp(
            pvpWeaponPositionTarget.y + pvpAdsWeaponRiseKick * safeStrength,
            0f,
            pvpAdsWeaponRiseKick * 2.75f);
        pvpWeaponPositionTarget.z = Mathf.Clamp(
            pvpWeaponPositionTarget.z - pvpAdsWeaponBackKick * safeStrength,
            -pvpAdsWeaponBackKick * 2.75f,
            0f);

        // 当帧立即响应，后续帧再由 UpdatePvpWeaponRecoil 平滑追随。
        pvpWeaponRecoilCurrent = Vector3.Lerp(
            pvpWeaponRecoilCurrent,
            pvpWeaponRecoilTarget,
            0.55f);
        pvpWeaponPositionCurrent = Vector3.Lerp(
            pvpWeaponPositionCurrent,
            pvpWeaponPositionTarget,
            0.55f);
    }

    /// <summary>
    /// 在 Animator 和全息瞄具基础对齐之后，让整把枪（包括镜框和红点）共同承受
    /// 旋转与位移冲击。这样不会再出现画面已经震动、瞄具却被钉死在屏幕中心的割裂感。
    /// </summary>
    public void ApplyPvpWeaponRecoilPose(bool shouldApply)
    {
        if (!shouldApply || !CanAlignPvpHolographicSight ||
            (pvpWeaponRecoilCurrent.sqrMagnitude <= 0.000001f &&
             pvpWeaponPositionCurrent.sqrMagnitude <= 0.00000001f))
        {
            return;
        }

        pvpWeaponPreRecoilLocalPosition = transform.localPosition;
        pvpWeaponPreRecoilLocalRotation = transform.localRotation;
        Vector3 aimPointBeforeRecoil = holographicAimPoint.position;
        transform.localPosition = pvpWeaponPreRecoilLocalPosition + pvpWeaponPositionCurrent;
        transform.localRotation = pvpWeaponPreRecoilLocalRotation *
                                  Quaternion.Euler(pvpWeaponRecoilCurrent);

        // 让枪身围绕瞄具中心承受后坐：保留旋转和前后冲击，只抵消会让红点
        // 离开屏幕中心的横向、纵向位移。
        Transform cameraParent = transform.parent;
        if (cameraParent != null)
        {
            Vector3 beforeInCamera = cameraParent.InverseTransformPoint(aimPointBeforeRecoil);
            Vector3 afterInCamera = cameraParent.InverseTransformPoint(holographicAimPoint.position);
            transform.localPosition += new Vector3(
                beforeInCamera.x - afterInCamera.x,
                beforeInCamera.y - afterInCamera.y,
                0f);
        }
        pvpWeaponRecoilPoseApplied = true;
    }

    /// <summary>
    /// Animator 完成本帧抬枪动作后，再让镜内准心沿单一路径平滑靠近屏幕中心。
    /// SmoothDamp 不会越过目标；松开瞄准时也会平滑撤销剩余修正量。
    /// </summary>
    public void UpdateHolographicAlignment(Transform cameraTransform, bool shouldAim)
    {
        if (!CanAlignPvpHolographicSight || cameraTransform == null)
        {
            ResetHolographicAlignment();
            return;
        }

        Vector3 currentAimPoint = cameraTransform.InverseTransformPoint(holographicAimPoint.position);
        if (shouldAim && !holographicAlignmentActive)
        {
            holographicAlignmentActive = true;
            smoothedHolographicAimPoint = currentAimPoint;
            holographicAlignmentVelocity = Vector3.zero;
        }

        if (!holographicAlignmentActive)
            return;

        Vector3 targetAimPoint = shouldAim
            ? new Vector3(0f, 0f, pvpHolographicEyeRelief)
            : currentAimPoint;
        smoothedHolographicAimPoint = Vector3.SmoothDamp(
            smoothedHolographicAimPoint,
            targetAimPoint,
            ref holographicAlignmentVelocity,
            Mathf.Max(0.01f, pvpHolographicAimSmoothTime),
            Mathf.Infinity,
            Time.deltaTime);

        // viewmodel 是相机的直接子节点，相机本地空间位移可直接用于根节点 localPosition。
        // 开火动画会大幅移动枪根节点，因此瞄准期间必须每帧重新校准，保证瞄具始终在屏幕中心。
        lastHolographicAlignmentCorrection = smoothedHolographicAimPoint - currentAimPoint;
        transform.localPosition += lastHolographicAlignmentCorrection;

        if (!shouldAim &&
            lastHolographicAlignmentCorrection.sqrMagnitude < 0.000001f &&
            holographicAlignmentVelocity.sqrMagnitude < 0.0001f)
        {
            holographicAlignmentActive = false;
            holographicAlignmentVelocity = Vector3.zero;
        }
    }

    private void ResetHolographicAlignment()
    {
        ClearHolographicAlignmentCorrection();
        holographicAlignmentActive = false;
        holographicAlignmentVelocity = Vector3.zero;
        smoothedHolographicAimPoint = Vector3.zero;
        hasPvpRenderedAimDirection = false;
    }

    public void ResetPvpWeaponRecoil()
    {
        // 后坐姿势最后施加，重置时先移除它，再撤销瞄具基础对齐。
        ClearPvpWeaponRecoilPose();
        ClearHolographicAlignmentCorrection();
        pvpWeaponRecoilTarget = Vector3.zero;
        pvpWeaponRecoilCurrent = Vector3.zero;
        pvpWeaponPositionTarget = Vector3.zero;
        pvpWeaponPositionCurrent = Vector3.zero;
        hasPvpRenderedAimDirection = false;
    }

    /// <summary>供 viewmodel 生成器判断哪些 Scope 资源属于固定全息瞄具。</summary>
    public static bool IsHolographicSightRendererName(string objectName)
    {
        if (string.IsNullOrEmpty(objectName))
            return false;

        string lowerName = objectName.ToLowerInvariant();
        return lowerName == "scope_02" || lowerName.StartsWith("scope 2 ");
    }

    private void ApplyAttachmentVisibility()
    {
        if (viewmodelTransforms != null)
        {
            foreach (Transform child in viewmodelTransforms)
            {
                if (child == null)
                    continue;
                string objectName = child.gameObject.name.ToLowerInvariant();
                if (objectName.StartsWith("scope") && objectName.Contains("render mesh"))
                {
                    bool selectedScope = pvpHolographicSightEnabled &&
                        IsHolographicSightRendererName(objectName);
                    child.gameObject.SetActive(selectedScope);
                }
            }
        }

        if (viewmodelRenderers == null)
            return;

        for (int i = 0; i < viewmodelRenderers.Length; i++)
        {
            Renderer renderer = viewmodelRenderers[i];
            if (renderer == null)
                continue;

            string objectName = renderer.gameObject.name.ToLowerInvariant();
            if (objectName.Contains("knife") || objectName == "silencer")
            {
                renderer.enabled = false;
                continue;
            }

            if (objectName.StartsWith("scope"))
            {
                bool selectedScope = pvpHolographicSightEnabled &&
                    IsHolographicSightRendererName(objectName);
                bool isReticle = objectName == "scope 2 sight texture";
                renderer.enabled = selectedScope && (!isReticle || aiming);
                if (selectedScope && isReticle)
                {
                    if (renderer is SpriteRenderer spriteRenderer && pvpHolographicReticle != null)
                        spriteRenderer.sprite = pvpHolographicReticle;
                    renderer.transform.localScale = Vector3.one * 0.01f;
                }
                continue;
            }

            if (objectName.Contains("iron_sights"))
                renderer.enabled = !pvpHolographicSightEnabled && initialRendererStates[i];
        }
    }

    /// <summary>开火动画 + 枪口火花（开镜时播放 Aim Fire，否则播放腰射 Fire）</summary>
    public void Fire()
    {
        if (animator != null)
        {
            // Scope 2 的原始 Aim Fire 动画包含大幅左下位移，会破坏固定瞄准位置。
            // PVP 开镜时保持 Aim Scope 2 姿态，后坐由镜头和瞄具中心枢轴共同表现。
            if (!(aiming && pvpHolographicSightEnabled))
            {
                string stateName = aiming ? "Aim Fire" : "Fire";
                animator.Play(stateName, 0, 0f);
            }
        }
        if (muzzleFlash != null)
            muzzleFlash.Emit(1);
        if (sparkParticles != null)
            sparkParticles.Emit(1);
    }

    /// <summary>
    /// 播放空仓/非空仓换弹动画，并返回实际动画片段时长，供弹药逻辑在动画结束时补弹。
    /// </summary>
    public float PlayReload(bool emptyMagazine, float fallbackDuration)
    {
        aiming = false;
        if (animator == null)
            return fallbackDuration;

        animator.SetBool(aimHash, false);
        animator.SetBool(aimScope2Hash, false);
        animator.SetBool(walkHash, false);
        animator.SetBool(runHash, false);
        ApplyAttachmentVisibility();
        animator.Play(emptyMagazine ? "Reload Out Of Ammo" : "Reload Ammo Left", 0, 0f);

        string expectedClipName = emptyMagazine ? "reload_out_of_ammo" : "reload_ammo_left";
        RuntimeAnimatorController controller = animator.runtimeAnimatorController;
        if (controller != null)
        {
            foreach (AnimationClip clip in controller.animationClips)
            {
                if (clip != null && clip.name.ToLowerInvariant().Contains(expectedClipName))
                    return clip.length;
            }
        }
        return fallbackDuration;
    }

    public void SetAiming(bool aiming)
    {
        bool aimingChanged = this.aiming != aiming;
        this.aiming = aiming;
        if (animator != null)
        {
            animator.SetBool(aimHash, aiming && !pvpHolographicSightEnabled);
            animator.SetBool(aimScope2Hash, aiming && pvpHolographicSightEnabled);
        }
        if (aimingChanged)
        {
            ApplyAttachmentVisibility();
            if (aiming && pvpHolographicSightEnabled && aimInSound != null && aimAudioSource != null)
                aimAudioSource.PlayOneShot(aimInSound, aimInVolume);
        }
    }

    public void SetMoving(bool moving)
    {
        if (animator != null)
            animator.SetBool(walkHash, moving);
    }

    public void SetRunning(bool running)
    {
        if (animator != null)
            animator.SetBool(runHash, running);
    }
}
