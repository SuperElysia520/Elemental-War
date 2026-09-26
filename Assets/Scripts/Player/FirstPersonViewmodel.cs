using UnityEngine;

/// <summary>
/// 第一人称 viewmodel（手臂+枪）：只做本地渲染与开火视觉，不联网。
/// 由 PlayerController 实例化并挂在本地主相机下。
/// 动画参数与 Low Poly FPS Pack 的 Animator Controller 对齐：Aim/Walk/Run 为 Bool，开火用 "Fire" 状态。
/// </summary>
public class FirstPersonViewmodel : MonoBehaviour
{
    public const float PvpHolographicMagnification = 1.5f;

    [System.Serializable]
    public struct PvpShotFeel
    {
        [Tooltip("每发旋转冲击：X=抬枪，Y=左右偏转，Z=侧倾（填写正数）")]
        public Vector3 rotationKick;
        [Tooltip("每发旋转随机量；只改变视觉，不改变真实弹道")]
        public Vector3 rotationJitter;
        [Tooltip("每发位移冲击：X=左右，Y=向上，Z=向后（填写正数）")]
        public Vector3 positionKick;
        [Tooltip("连续射击允许累积到的最大旋转角度")]
        public Vector3 maxRotation;
        [Tooltip("连续射击允许累积到的最大位移")]
        public Vector3 maxPosition;
        [Min(0.01f)] public float kickSpeed;
        [Min(0.01f)] public float returnSpeed;
        [Range(0f, 1f)] public float immediateResponse;
        [Range(0f, 0.1f)] public float sustainedBuildUpPerShot;
    }

    [Tooltip("枪口（子弹生成点）")] public Transform bulletSpawnPoint;
    [Tooltip("枪口火花粒子")] public ParticleSystem muzzleFlash;
    [Tooltip("开火火花粒子")] public ParticleSystem sparkParticles;
    [Tooltip("抛壳位置")]
    public Transform casingSpawnPoint;
    [Tooltip("每发抛出的弹壳预制体")]
    public Transform casingPrefab;

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

    [Header("PVP 腰射开火手感")]
    [Tooltip("腰射使用更明显的后移、抬枪和侧摆，模拟参考视频的重心冲击")]
    public PvpShotFeel pvpHipFireFeel = new PvpShotFeel
    {
        rotationKick = new Vector3(2.35f, 0.65f, 1.4f),
        rotationJitter = new Vector3(0.16f, 0.12f, 0.2f),
        positionKick = new Vector3(0.006f, 0.004f, 0.022f),
        maxRotation = new Vector3(6.5f, 2.8f, 5.5f),
        maxPosition = new Vector3(0.025f, 0.016f, 0.07f),
        kickSpeed = 52f,
        returnSpeed = 16f,
        immediateResponse = 0.72f,
        sustainedBuildUpPerShot = 0.035f,
    };

    [Header("PVP 瞄准开火手感")]
    [Tooltip("开镜时围绕全息准心做短促冲击；画面保持可读，枪身仍有清晰重量")]
    public PvpShotFeel pvpAdsFireFeel = new PvpShotFeel
    {
        rotationKick = new Vector3(1.35f, 0.43f, 1f),
        rotationJitter = new Vector3(0.13f, 0.1f, 0.16f),
        positionKick = new Vector3(0.0022f, 0.0017f, 0.0135f),
        maxRotation = new Vector3(5f, 2f, 6f),
        maxPosition = new Vector3(0.01f, 0.015f, 0.05f),
        kickSpeed = 100f,
        returnSpeed = 5.5f,
        immediateResponse = 0.74f,
        sustainedBuildUpPerShot = 0.045f,
    };
    [Range(0f, 1f)]
    [Tooltip("ADS 对红点横纵位移的稳定比例：1=完全固定，0=完全跟随枪身。本项目建议保留部分晃动。")]
    public float pvpAdsReticleStabilization = 1f;

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
    private bool pvpWeaponRecoilAiming;
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

    public bool TryGetPvpRenderedAimDirection(Quaternion cameraRotation, out Vector3 worldDirection)
    {
        if (hasPvpRenderedAimDirection)
        {
            worldDirection = (cameraRotation * pvpRenderedAimDirectionLocal).normalized;
            return true;
        }

        worldDirection = cameraRotation * Vector3.forward;
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
        PvpShotFeel feel = pvpWeaponRecoilAiming ? pvpAdsFireFeel : pvpHipFireFeel;
        float returnT = 1f - Mathf.Exp(-Mathf.Max(0.01f, feel.returnSpeed) * deltaTime);
        float kickT = 1f - Mathf.Exp(-Mathf.Max(0.01f, feel.kickSpeed) * deltaTime);
        pvpWeaponRecoilTarget = Vector3.Lerp(pvpWeaponRecoilTarget, Vector3.zero, returnT);
        pvpWeaponRecoilCurrent = Vector3.Lerp(pvpWeaponRecoilCurrent, pvpWeaponRecoilTarget, kickT);
        pvpWeaponPositionTarget = Vector3.Lerp(pvpWeaponPositionTarget, Vector3.zero, returnT);
        pvpWeaponPositionCurrent = Vector3.Lerp(pvpWeaponPositionCurrent, pvpWeaponPositionTarget, kickT);
    }

    /// <summary>
    /// 腰射与开镜共用的逐发视觉冲击。真实镜头后坐由 PlayerController 处理，
    /// 这里仅驱动枪模，因此可以做出参考视频中更明显的后移、抬枪和持续侧倾。
    /// </summary>
    public void AddPvpShotRecoil(
        bool isAiming,
        float horizontalDirection,
        float horizontalAmount,
        int shotIndex)
    {
        if (!pvpHolographicSightEnabled)
            return;

        pvpWeaponRecoilAiming = isAiming;
        PvpShotFeel feel = isAiming ? pvpAdsFireFeel : pvpHipFireFeel;
        float side = Mathf.Abs(horizontalDirection) > 0.001f
            ? Mathf.Sign(horizontalDirection)
            : (Random.value < 0.5f ? -1f : 1f);
        float sideWeight = Mathf.Lerp(0.78f, 1.18f, Mathf.Clamp01(horizontalAmount));
        float sustainedStrength = 1f +
            Mathf.Min(Mathf.Max(0, shotIndex), 16) * feel.sustainedBuildUpPerShot;

        float pitchKick = Mathf.Max(
            0f,
            feel.rotationKick.x + Random.Range(-feel.rotationJitter.x, feel.rotationJitter.x));
        float yawKick = side * feel.rotationKick.y * sideWeight +
                        Random.Range(-feel.rotationJitter.y, feel.rotationJitter.y);
        float rollKick = -side * feel.rotationKick.z * sideWeight +
                         Random.Range(-feel.rotationJitter.z, feel.rotationJitter.z);
        Vector3 rotationDelta = new Vector3(-pitchKick, yawKick, rollKick) * sustainedStrength;
        pvpWeaponRecoilTarget += rotationDelta;
        pvpWeaponRecoilTarget.x = Mathf.Clamp(
            pvpWeaponRecoilTarget.x,
            -Mathf.Abs(feel.maxRotation.x),
            0f);
        pvpWeaponRecoilTarget.y = Mathf.Clamp(
            pvpWeaponRecoilTarget.y,
            -Mathf.Abs(feel.maxRotation.y),
            Mathf.Abs(feel.maxRotation.y));
        pvpWeaponRecoilTarget.z = Mathf.Clamp(
            pvpWeaponRecoilTarget.z,
            -Mathf.Abs(feel.maxRotation.z),
            Mathf.Abs(feel.maxRotation.z));

        Vector3 positionDelta = new Vector3(
            side * feel.positionKick.x * sideWeight,
            feel.positionKick.y,
            -Mathf.Abs(feel.positionKick.z)) * sustainedStrength;
        pvpWeaponPositionTarget += positionDelta;
        pvpWeaponPositionTarget.x = Mathf.Clamp(
            pvpWeaponPositionTarget.x,
            -Mathf.Abs(feel.maxPosition.x),
            Mathf.Abs(feel.maxPosition.x));
        pvpWeaponPositionTarget.y = Mathf.Clamp(
            pvpWeaponPositionTarget.y,
            0f,
            Mathf.Abs(feel.maxPosition.y));
        pvpWeaponPositionTarget.z = Mathf.Clamp(
            pvpWeaponPositionTarget.z,
            -Mathf.Abs(feel.maxPosition.z),
            0f);

        // 当帧立即响应，后续帧再由 UpdatePvpWeaponRecoil 平滑追随。
        pvpWeaponRecoilCurrent = Vector3.Lerp(
            pvpWeaponRecoilCurrent,
            pvpWeaponRecoilTarget,
            feel.immediateResponse);
        pvpWeaponPositionCurrent = Vector3.Lerp(
            pvpWeaponPositionCurrent,
            pvpWeaponPositionTarget,
            feel.immediateResponse);
    }

    /// <summary>
    /// 在 Animator 和全息瞄具基础对齐之后，让整把枪（包括镜框和红点）共同承受
    /// 旋转与位移冲击。这样不会再出现画面已经震动、瞄具却被钉死在屏幕中心的割裂感。
    /// </summary>
    public void ApplyPvpWeaponRecoilPose(bool shouldApply, bool shouldAim)
    {
        if (!shouldApply || !pvpHolographicSightEnabled ||
            (pvpWeaponRecoilCurrent.sqrMagnitude <= 0.000001f &&
             pvpWeaponPositionCurrent.sqrMagnitude <= 0.00000001f))
        {
            return;
        }

        pvpWeaponPreRecoilLocalPosition = transform.localPosition;
        pvpWeaponPreRecoilLocalRotation = transform.localRotation;
        Vector3 aimPointBeforeRecoil = shouldAim && CanAlignPvpHolographicSight
            ? holographicAimPoint.position
            : Vector3.zero;
        transform.localPosition = pvpWeaponPreRecoilLocalPosition + pvpWeaponPositionCurrent;
        transform.localRotation = pvpWeaponPreRecoilLocalRotation *
                                  Quaternion.Euler(pvpWeaponRecoilCurrent);

        // 让枪身围绕瞄具中心承受后坐：保留旋转和前后冲击，只抵消会让红点
        // 离开屏幕中心的横向、纵向位移。腰射不做这层补偿，枪模可以完整摆动。
        Transform cameraParent = transform.parent;
        if (shouldAim && CanAlignPvpHolographicSight && cameraParent != null)
        {
            Vector3 beforeInCamera = cameraParent.InverseTransformPoint(aimPointBeforeRecoil);
            Vector3 afterInCamera = cameraParent.InverseTransformPoint(holographicAimPoint.position);
            Vector3 reticleCorrection = new Vector3(
                beforeInCamera.x - afterInCamera.x,
                beforeInCamera.y - afterInCamera.y,
                0f);
            transform.localPosition += reticleCorrection *
                                       Mathf.Clamp01(pvpAdsReticleStabilization);
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
        pvpWeaponRecoilAiming = aiming;
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
            // PVP 腰射与开镜都由同一套程序化冲击驱动，避免旧 Fire/Aim Fire 动画
            // 在两种姿势间出现节奏和位移断层。PVE 继续复用资源包原始动画。
            if (!pvpHolographicSightEnabled)
            {
                string stateName = aiming ? "Aim Fire" : "Fire";
                animator.Play(stateName, 0, 0f);
            }
        }
        if (muzzleFlash != null)
            muzzleFlash.Emit(1);
        if (sparkParticles != null)
            sparkParticles.Emit(1);
        if (casingSpawnPoint != null && casingPrefab != null)
            Instantiate(casingPrefab, casingSpawnPoint.position, casingSpawnPoint.rotation);
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
        if (aimingChanged && pvpHolographicSightEnabled)
            ResetPvpWeaponRecoil();
        this.aiming = aiming;
        if (animator != null)
        {
            animator.SetBool(aimHash, aiming && !pvpHolographicSightEnabled);
            animator.SetBool(aimScope2Hash, aiming && pvpHolographicSightEnabled);
        }
        if (aimingChanged)
            ApplyAttachmentVisibility();
    }

    /// <summary>
    /// 只由真实的瞄准按键按下沿调用。状态机切换不得直接播放，避免 Hover/Aiming 过渡叠音。
    /// </summary>
    public void PlayAimInSound()
    {
        if (!pvpHolographicSightEnabled || aimInSound == null || aimAudioSource == null)
            return;

        // 快速重复按键时用新声音替换旧声音，不允许多个尖锐瞬态叠加。
        aimAudioSource.Stop();
        aimAudioSource.clip = aimInSound;
        aimAudioSource.volume = aimInVolume;
        aimAudioSource.Play();
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
