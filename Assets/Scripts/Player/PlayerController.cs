using System.Collections;
using System.Collections.Generic;
using Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 玩家控制器（第一人称）
/// 视角：手动驱动主相机（俯仰 pitch + 偏航 yaw），身体 yaw 跟随相机。
/// 移动：计算相机相对方向，写入 PlayerModel.externalMoveVelocity，由 OnAnimatorMove 驱动（不再依赖第三人称 root motion 转向）。
/// 手臂：实例化 FirstPersonViewmodel 挂在主相机下，只做本地渲染。
/// </summary>
// 先于 MonoManager（默认顺序 0）更新：确保本帧读到的输入（尤其是 triggered 的跳跃）
// 在状态机 Update 执行前就绪，避免 PlayerController.Update 与 MonoManager.Update
// 执行顺序不定导致「按下当帧为真」的跳跃输入被丢帧。
[DefaultExecutionOrder(-100)]
public class PlayerController : SingleMonoBase<PlayerController>
{
    public PlayerModel currentPlayerModel; //当前操控的角色模型

    [Tooltip("正常视角相机（旧第三人称，已停用）")] public CinemachineFreeLook freeLookCamera;
    [Tooltip("瞄准视角相机（旧第三人称，已停用）")] public CinemachineFreeLook aimingCamera;

    #region 第一人称相关
    [Tooltip("第一人称手臂预制体（由菜单 联机 > 生成第一人称手臂预制体 生成）")]
    public GameObject viewmodelPrefab;
    [Tooltip("腰射时手臂相对主相机的本地偏移")] public Vector3 viewmodelOffset = new Vector3(-0.02f, -0.1f, 0.04f);
    [Tooltip("开镜时的手臂偏移，单独保留以避免破坏准星对齐")] public Vector3 viewmodelAimOffset = new Vector3(0f, -0.12f, 0.35f);
    [Tooltip("腰射/开镜持枪位置过渡速度")] public float viewmodelOffsetLerpSpeed = 12f;
    [Tooltip("手臂整体缩放（模型尺寸不合时调整）")] public float viewmodelScale = 1f;
    [Tooltip("眼睛高度（相对角色脚底）")] public float eyeHeight = 1.3f;
    [Tooltip("鼠标灵敏度")] public float mouseSensitivity = 0.1f;
    [Tooltip("俯仰角下限")] public float pitchMin = -75f;
    [Tooltip("俯仰角上限")] public float pitchMax = 75f;
    [Tooltip("默认视野")] public float defaultFov = 60f;
    [Tooltip("瞄准视野")] public float aimFov = 40f;
    [Tooltip("视野过渡速度")] public float fovLerpSpeed = 12f;

    #region 开火后坐力相关
    [Header("开火后坐力")]
    [Tooltip("每发向上抬起的角度")]
    public float recoilPitchPerShot = 0.9f;
    [Tooltip("每发水平弹道偏移角度")]
    public float recoilYawPerShot = 0.75f;
    [Tooltip("连续射击最大上抬角度")]
    public float recoilMaxPitch = 10.5f;
    [Tooltip("连续射击最大水平偏移角度")]
    public float recoilMaxYaw = 4.4f;
    [Tooltip("真实准心后坐力踢起速度")]
    public float recoilKickSpeed = 30f;
    [Tooltip("停火后的真实准心回正速度")]
    public float recoilReturnSpeed = 7.5f;
    [Range(0.1f, 1f)]
    [Tooltip("开镜时的真实准心后坐力倍率")]
    public float aimingRecoilMultiplier = 0.82f;
    [Tooltip("停火后多久开始回正")]
    public float recoilRecoveryDelay = 0.08f;
    [Tooltip("停火多久后重置为弹道第一发")]
    public float recoilPatternResetDelay = 0.3f;
    [Tooltip("固定后坐力弹道：X 为左右，Y 为向上")]
    public Vector2[] recoilPattern =
    {
        new Vector2(0.00f, 0.55f), new Vector2(-0.08f, 0.64f), new Vector2(0.12f, 0.70f),
        new Vector2(-0.18f, 0.76f), new Vector2(0.24f, 0.80f), new Vector2(0.34f, 0.76f),
        new Vector2(0.42f, 0.68f), new Vector2(0.30f, 0.62f), new Vector2(0.08f, 0.58f),
        new Vector2(-0.26f, 0.55f), new Vector2(-0.46f, 0.52f), new Vector2(-0.56f, 0.50f),
        new Vector2(-0.34f, 0.48f), new Vector2(0.06f, 0.46f), new Vector2(0.44f, 0.44f),
        new Vector2(0.58f, 0.42f), new Vector2(0.38f, 0.40f), new Vector2(0.00f, 0.38f),
        new Vector2(-0.38f, 0.36f), new Vector2(-0.56f, 0.34f)
    };

    [Header("PVP 射击手感")]
    [Tooltip("PVP 垂直后坐力倍率")]
    public float pvpVerticalRecoilMultiplier = 1.5f;
    [Tooltip("PVP 水平弹道倍率；用于让连续射击出现清晰的左右摆动")]
    public float pvpHorizontalRecoilMultiplier = 1.85f;
    [Tooltip("PVP 开镜时在原 aimingRecoilMultiplier 基础上的补偿倍率")]
    public float pvpAimingRecoilMultiplier = 0.75f;
    [Tooltip("每发附加的轻微随机水平冲击角度，不影响准心与落点一致性")]
    public float pvpHorizontalJitter = 0.03f;
    [Tooltip("每发瞬时镜头上跳角度")]
    public float pvpCameraImpulsePitch = 0.32f;
    [Tooltip("每发瞬时镜头左右冲击角度")]
    public float pvpCameraImpulseYaw = 0.08f;
    [Tooltip("每发瞬时镜头侧倾角度")]
    public float pvpCameraImpulseRoll = 0.16f;
    [Tooltip("每发镜头向后位移距离")]
    public float pvpCameraKickback = 0.012f;
    [Tooltip("瞬时镜头冲击恢复速度")]
    public float pvpCameraImpulseReturnSpeed = 24f;
    #endregion
    

    private FirstPersonViewmodel viewmodel;
    private Camera mainCamera;
    private float yaw;
    private float pitch;
    private Vector2 recoilTarget;
    private Vector2 recoilCurrent;
    private Vector3 cameraRecoilImpulse;
    private float cameraKickbackImpulse;
    private int recoilShotIndex;
    private float lastRecoilShotTime = -999f;
    private readonly RaycastHit[] aimHits = new RaycastHit[32];
    private readonly RaycastHit[] pvpPredictionHits = new RaycastHit[32];
    private PlayerModel hiddenBody; // 当前被隐藏渲染的身体（本地不显示自己的第三人称身体）
    private Coroutine autoReloadCoroutine;
    private PlayerWeapon autoReloadWeapon;
    private GameObject pvpCrosshair;
    private GameObject pvpCrosshairPoint;
    private bool pvpCrosshairInitialActive;
    private bool pvpCrosshairPointInitialActive;
    #endregion

    #region 玩家输入相关
    private MyInputSystem input; // 输入系统
    [HideInInspector] public Vector2 moveInput; // 移动输入
    [HideInInspector] public bool isSprint; // 冲刺输入
    [HideInInspector] public bool isAiming; // 瞄准输入
    [HideInInspector] public bool isJumping; // 跳跃输入
    [HideInInspector] public bool isFire;
    #endregion

    #region 瞄准相关
    [Tooltip("瞄准目标")] public Transform AimTarget;
    [Tooltip("射线检测的最大距离")] public float maxRayDistance = 1000f;
    [Tooltip("射线检测的层级")] public LayerMask aimLayerMask = ~0;
    #endregion

    #region 开火抖动
    private CinemachineImpulseSource impulseSource;
    #endregion

    [Tooltip("转向速度")] public float rotationSpeed = 300f;

    [Tooltip("联机模式下禁用 1/2/3 切换（一人一角，由 PlayerNetworkSync 设置）")]
    [HideInInspector] public bool disableModelSwitching;

    [HideInInspector] public Vector3 localMovement; // 本地空间下的玩家移动方向
    [HideInInspector] public Vector3 worldMovement; // 世界空间下的玩家移动方向

    protected override void Awake()
    {
        base.Awake();
        input = new MyInputSystem();
    }

    void Start()
    {
        mainCamera = Camera.main;
        Cursor.lockState = CursorLockMode.Locked; // 锁定光标

        // 停用旧第三人称相机，改由本组件手动驱动主相机
        if (freeLookCamera != null) freeLookCamera.Priority = 0;
        if (aimingCamera != null) aimingCamera.Priority = 0;
        if (aimingCamera != null) impulseSource = aimingCamera.GetComponent<CinemachineImpulseSource>();

        if (mainCamera != null)
        {
            mainCamera.fieldOfView = defaultFov;

            // 停用 CinemachineBrain：纯第一人称下主相机由本组件在 LateUpdate 手动驱动。
            // 否则场景里残留的 vcam（FreeLook/Aiming 等）会在 LateUpdate 覆盖相机，
            // 造成「按 W 的前进方向与画面实际朝向不一致」。
            var brain = mainCamera.GetComponent<CinemachineBrain>();
            if (brain != null)
                brain.enabled = false;
        }

        if (currentPlayerModel != null)
            yaw = currentPlayerModel.transform.eulerAngles.y;
        pitch = 0f;

        ExitAim();
        SetupLocalView();
    }

    void Update()
    {
        // 退出游戏/销毁阶段 currentPlayerModel 可能已被销毁，避免访问已销毁对象
        if (currentPlayerModel == null)
            return;

        #region 更新玩家输入
        moveInput = input.Player.Move.ReadValue<Vector2>().normalized;
        PlayerWeapon currentWeapon = currentPlayerModel.weapon;
        if (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame)
            TryReload();
        bool weaponReloading = currentWeapon != null && currentWeapon.IsReloading;
        bool sprintPressed = input.Player.IsSprint.IsPressed();
        isAiming = input.Player.IsAiming.IsPressed() && !weaponReloading;
        isJumping = input.Player.IsJumping.triggered;
        isFire = input.Player.Fire.IsPressed() && !weaponReloading;
        // CS 式射击不允许边奔跑边保持冲刺：开镜/开火会终止冲刺，且只允许向前冲刺。
        isSprint = sprintPressed && moveInput.y > 0.1f && !isAiming && !isFire && !weaponReloading;
        #endregion

        #region 视角（鼠标 delta）
        Vector2 look = input.Player.Look.ReadValue<Vector2>();
        yaw += look.x * mouseSensitivity;
        pitch = Mathf.Clamp(pitch - look.y * mouseSensitivity, pitchMin, pitchMax);

        // 射击期间不自动回正；停火稍微延迟后才恢复，玩家可以反向移动鼠标压枪。
        float returnT = 1f - Mathf.Exp(-recoilReturnSpeed * Time.deltaTime);
        float kickT = 1f - Mathf.Exp(-recoilKickSpeed * Time.deltaTime);
        if (Time.time - lastRecoilShotTime >= recoilRecoveryDelay)
            recoilTarget = Vector2.Lerp(recoilTarget, Vector2.zero, returnT);
        if (Time.time - lastRecoilShotTime >= recoilPatternResetDelay)
            recoilShotIndex = 0;
        recoilCurrent = Vector2.Lerp(recoilCurrent, recoilTarget, kickT);
        float cameraImpulseT = 1f - Mathf.Exp(
            -Mathf.Max(0.01f, pvpCameraImpulseReturnSpeed) * Time.deltaTime);
        cameraRecoilImpulse = Vector3.Lerp(cameraRecoilImpulse, Vector3.zero, cameraImpulseT);
        cameraKickbackImpulse = Mathf.Lerp(cameraKickbackImpulse, 0f, cameraImpulseT);
        // 身体朝向始终跟随相机 yaw（第一人称）
        currentPlayerModel.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        #endregion

        #region 计算移动方向（相机相对，无需读相机状态）
        Vector3 forward = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
        Vector3 right = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
        worldMovement = forward * moveInput.y + right * moveInput.x;
        localMovement = currentPlayerModel.transform.InverseTransformDirection(worldMovement);
        // 外部速度驱动水平移动（OnAnimatorMove 读取）
        float speed = isSprint ? currentPlayerModel.sprintSpeed : currentPlayerModel.moveSpeed;
        currentPlayerModel.externalMoveVelocity = worldMovement * speed;
        #endregion

        #region 手臂动画（本地视觉）
        if (viewmodel != null)
        {
            bool moving = moveInput.sqrMagnitude > 0.0001f;
            viewmodel.ClearPvpWeaponRecoilPose();
            viewmodel.ClearHolographicAlignmentCorrection();
            viewmodel.UpdatePvpWeaponRecoil(Time.deltaTime);
            bool usesPvpHolographicAlignment = viewmodel.CanAlignPvpHolographicSight;
            // 全息瞄具由自身动画抬枪，根节点保持腰射基础位置；动画后的精确居中在 LateUpdate 完成。
            Vector3 targetOffset = isAiming && !usesPvpHolographicAlignment
                ? viewmodelAimOffset
                : viewmodelOffset;
            float offsetT = 1f - Mathf.Exp(-viewmodelOffsetLerpSpeed * Time.deltaTime);
            viewmodel.transform.localPosition = Vector3.Lerp(
                viewmodel.transform.localPosition,
                targetOffset,
                offsetT);
            // Aim 只由右键决定，左键腰射不改变开镜参数。
            viewmodel.SetAiming(isAiming);
            viewmodel.SetMoving(moving && !isSprint && !weaponReloading);
            viewmodel.SetRunning(moving && isSprint && !weaponReloading);
            UpdatePvpCrosshairVisibility();
        }
        #endregion

        #region 开火
        // 腰射与开镜共用唯一开火入口，避免重复判定射速/散布/后坐力。
        if (isFire)
            TryFire();
        #endregion

        #region 切换角色输入监听
        if (disableModelSwitching)
            return;

        if (input.Player.First.triggered)
        {
            SwitchPlayerModel(0);
        }
        else if (input.Player.Second.triggered)
        {
            SwitchPlayerModel(1);
        }
        else if (input.Player.Thrid.triggered)
        {
            SwitchPlayerModel(2);
        }
        #endregion
    }

    void LateUpdate()
    {
        if (currentPlayerModel == null || mainCamera == null)
            return;

        // 相机跟随角色眼睛。持续后坐力负责弹道爬升，瞬时冲击负责每发的震动、侧倾与后移。
        Vector3 eyePosition = GetCurrentEyePosition();
        Quaternion cameraRotation = GetCurrentCameraRotation();
        mainCamera.transform.rotation = cameraRotation;
        mainCamera.transform.position = eyePosition + cameraRotation * (Vector3.back * cameraKickbackImpulse);

        // Animator 已计算完本帧姿势：先完成静态开镜对齐，再让镜框、红点和整枪一起承受后坐。
        if (viewmodel != null)
        {
            bool isPvp = IsCurrentPlayerPvp();
            viewmodel.UpdateHolographicAlignment(mainCamera.transform, isAiming && isPvp);
            viewmodel.ApplyPvpWeaponRecoilPose(isPvp, isAiming);
            viewmodel.CapturePvpRenderedAimDirection(
                mainCamera.transform,
                isAiming && isPvp);
        }

        // PVP 固定全息瞄具按 1.5x 计算 FOV；PVE 继续使用原有 aimFov。
        float activeAimFov = viewmodel != null
            ? viewmodel.GetAimFieldOfView(defaultFov, aimFov)
            : aimFov;
        float targetFov = isAiming ? activeAimFov : defaultFov;
        mainCamera.fieldOfView = Mathf.Lerp(mainCamera.fieldOfView, targetFov, fovLerpSpeed * Time.deltaTime);
    }

    private Vector3 GetCurrentEyePosition()
    {
        return currentPlayerModel != null
            ? currentPlayerModel.transform.position + Vector3.up * eyeHeight
            : (mainCamera != null ? mainCamera.transform.position : transform.position);
    }

    /// <summary>
    /// 使用本帧已经读取到的鼠标输入和后坐状态直接计算视角。
    /// 开火在 Update 发生，不能依赖上一帧 LateUpdate 才写入的 Camera.transform。
    /// </summary>
    private Quaternion GetCurrentCameraRotation()
    {
        float recoilPitch = Mathf.Clamp(
            pitch - recoilCurrent.y - cameraRecoilImpulse.x,
            pitchMin,
            pitchMax);
        return Quaternion.Euler(
            recoilPitch,
            yaw + recoilCurrent.x + cameraRecoilImpulse.y,
            cameraRecoilImpulse.z);
    }

    /// <summary>
    /// 切换角色
    /// </summary>
    /// <param name="index"></param>
    public void SwitchPlayerModel(int index)
    {
        if (index >= GameManager.instance.playerModels.Length || GameManager.instance.playerModels[index] == null)
            return;
        currentPlayerModel.Exit();
        currentPlayerModel = GameManager.instance.playerModels[index];
        currentPlayerModel.Enter();
        SetupLocalView();
    }

    /// <summary>
    /// 进入瞄准
    /// </summary>
    public void EnterAim()
    {
        if (currentPlayerModel != null)
            currentPlayerModel.EnterAim();
        if (viewmodel != null)
            viewmodel.SetAiming(true);
    }

    /// <summary>
    /// 退出瞄准
    /// </summary>
    public void ExitAim()
    {
        if (currentPlayerModel != null)
            currentPlayerModel.ExitAim();
        if (viewmodel != null)
            viewmodel.SetAiming(false);
    }

    /// <summary>
    /// 重置/建立本地第一人称视角（单机 Start 与联机 spawn 时都会调用）
    /// </summary>
    public void ResetCameraTarget()
    {
        SetupLocalView();
    }

    /// <summary>
    /// 建立本地第一人称视角：隐藏自己的第三人称身体，实例化手臂 viewmodel。
    /// </summary>
    private void SetupLocalView()
    {
        if (mainCamera == null)
            mainCamera = Camera.main;
        if (mainCamera == null)
            return;

        // 恢复上一个被隐藏身体的渲染，隐藏新操控身体
        if (hiddenBody != null && hiddenBody != currentPlayerModel)
            SetBodyRenderers(hiddenBody, true);
        if (currentPlayerModel != null)
        {
            // 受控角色启用代码驱动移动（关闭 AI 跟随，禁用 navMeshAgent），
            // 单机初始模型没有走 SwitchPlayerModel 的 Enter，这里补上（幂等）
            currentPlayerModel.Enter();
            SetBodyRenderers(currentPlayerModel, false);
            hiddenBody = currentPlayerModel;
        }

        // 实例化手臂 viewmodel 到主相机下
        if (viewmodel == null)
        {
            // 未在 Inspector 指派时，从 Resources 自动加载（由菜单生成到 Assets/Resources/Players/）
            if (viewmodelPrefab == null)
                viewmodelPrefab = Resources.Load<GameObject>("Players/Viewmodel_AssaultRifle");
            if (viewmodelPrefab != null)
            {
                GameObject go = Instantiate(viewmodelPrefab, mainCamera.transform);
                go.transform.localPosition = viewmodelOffset;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one * viewmodelScale;
                viewmodel = go.GetComponent<FirstPersonViewmodel>();
            }
        }

        if (viewmodel != null)
            viewmodel.ConfigurePvpHolographicSight(IsCurrentPlayerPvp());
        ResolvePvpCrosshair();
        UpdatePvpCrosshairVisibility();
    }

    private bool IsCurrentPlayerPvp()
    {
        return currentPlayerModel != null && currentPlayerModel.GetComponent<PvpPlayerCombat>() != null;
    }

    private void ResolvePvpCrosshair()
    {
        if (!IsCurrentPlayerPvp())
            return;

        if (pvpCrosshair == null)
        {
            pvpCrosshair = GameObject.Find("CrossHair");
            if (pvpCrosshair != null)
                pvpCrosshairInitialActive = pvpCrosshair.activeSelf;
        }
        if (pvpCrosshairPoint == null)
        {
            pvpCrosshairPoint = GameObject.Find("CrossHairPoint");
            if (pvpCrosshairPoint != null)
                pvpCrosshairPointInitialActive = pvpCrosshairPoint.activeSelf;
        }
    }

    private void UpdatePvpCrosshairVisibility()
    {
        if (!IsCurrentPlayerPvp())
            return;

        ResolvePvpCrosshair();
        bool showScreenCrosshair = !isAiming;
        if (pvpCrosshair != null)
            pvpCrosshair.SetActive(showScreenCrosshair && pvpCrosshairInitialActive);
        if (pvpCrosshairPoint != null)
            pvpCrosshairPoint.SetActive(showScreenCrosshair && pvpCrosshairPointInitialActive);
    }

    private void SetBodyRenderers(PlayerModel model, bool visible)
    {
        if (model == null)
            return;
        foreach (var r in model.GetComponentsInChildren<Renderer>(true))
            r.enabled = visible;
    }

    /// <summary>本地真实子弹的生成位置（优先 viewmodel 枪口）</summary>
    public Vector3 FireOrigin
    {
        get
        {
            if (viewmodel != null && viewmodel.bulletSpawnPoint != null && mainCamera != null)
            {
                // Viewmodel 是相机子节点：保留 Animator 算出的本地枪口姿势，再套用本帧
                // 鼠标/后坐视角，避免快速甩枪时曳光起点也落后一帧。
                Vector3 localMuzzle = mainCamera.transform.InverseTransformPoint(
                    viewmodel.bulletSpawnPoint.position);
                Quaternion currentRotation = GetCurrentCameraRotation();
                Vector3 currentCameraPosition = GetCurrentEyePosition() +
                    currentRotation * (Vector3.back * cameraKickbackImpulse);
                return currentCameraPosition + currentRotation * localMuzzle;
            }
            if (viewmodel != null && viewmodel.bulletSpawnPoint != null)
                return viewmodel.bulletSpawnPoint.position;
            return mainCamera != null ? mainCamera.transform.position : transform.position;
        }
    }

    /// <summary>本地开火方向（相机正前方）</summary>
    public Vector3 GetFireDirection(Vector3 fireOrigin)
    {
        if (mainCamera == null)
            return transform.forward;

        Ray centerRay = new Ray(
            GetCurrentEyePosition(),
            GetCurrentCameraRotation() * Vector3.forward);
        Vector3 targetPoint = centerRay.origin + centerRay.direction * maxRayDistance;
        float nearestDistance = maxRayDistance;

        int hitCount = Physics.RaycastNonAlloc(
            centerRay,
            aimHits,
            maxRayDistance,
            aimLayerMask,
            QueryTriggerInteraction.Ignore);

        for (int i = 0; i < hitCount; i++)
        {
            RaycastHit hit = aimHits[i];
            if (hit.collider == null || IsLocalPlayerCollider(hit.collider.transform))
                continue;

            if (hit.distance < nearestDistance)
            {
                nearestDistance = hit.distance;
                targetPoint = hit.point;
            }
        }

        Vector3 direction = targetPoint - fireOrigin;
        return direction.sqrMagnitude > 0.000001f ? direction.normalized : centerRay.direction;
    }

    private bool IsLocalPlayerCollider(Transform hitTransform)
    {
        if (hitTransform == null)
            return false;

        if (currentPlayerModel != null)
        {
            Transform bodyRoot = currentPlayerModel.transform;
            if (hitTransform == bodyRoot || hitTransform.IsChildOf(bodyRoot))
                return true;
        }

        if (viewmodel != null)
        {
            Transform viewRoot = viewmodel.transform;
            if (hitTransform == viewRoot || hitTransform.IsChildOf(viewRoot))
                return true;
        }

        return false;
    }

    /// <summary>触发手臂开火动画与枪口火花（本地视觉）</summary>
    public void PlayFireView()
    {
        if (viewmodel != null)
            viewmodel.Fire();
    }

    private void TryFire()
    {
        if (currentPlayerModel == null || currentPlayerModel.weapon == null)
            return;

        PlayerWeapon weapon = currentPlayerModel.weapon;
        if (weapon.IsReloading)
            return;
        if (weapon.CurrentAmmo <= 0)
        {
            QueueAutoReload(weapon);
            return;
        }

        Vector3 visualOrigin = FireOrigin;
        bool usePvpAimRay = mainCamera != null && IsCurrentPlayerPvp();
        Quaternion shotRotation = GetCurrentCameraRotation();
        Vector3 hitOrigin = usePvpAimRay ? GetCurrentEyePosition() : visualOrigin;
        Vector3 hitDirection = usePvpAimRay
            ? shotRotation * Vector3.forward
            : GetFireDirection(visualOrigin);
        if (usePvpAimRay && isAiming && viewmodel != null)
            viewmodel.TryGetPvpRenderedAimDirection(shotRotation, out hitDirection);
        bool grounded = currentPlayerModel.cc != null && currentPlayerModel.cc.isGrounded;
        bool moving = moveInput.sqrMagnitude > 0.0001f;
        // PVP 采用“准心即落点”：腰射走屏幕准心，开镜走实际渲染出的全息红点方向，
        // 不再额外叠加一层不可见随机散布。
        // PVE 保留原有移动、腾空与连续射击散布。
        float spread = usePvpAimRay
            ? 0f
            : weapon.CalculateSpread(
                moving,
                isSprint,
                grounded,
                isAiming,
                recoilShotIndex);

        bool fired = usePvpAimRay
            ? weapon.Fire(hitOrigin, hitDirection, visualOrigin, spread, true)
            : weapon.Fire(hitOrigin, hitDirection, spread);
        if (fired)
        {
            PlayFireView();
            if (usePvpAimRay)
                PredictPvpHitFeedback(hitOrigin, hitDirection, weapon);
            ShakeCamera();
            if (weapon.CurrentAmmo <= 0)
                QueueAutoReload(weapon);
        }
    }

    /// <summary>
    /// 本地只预测血花反馈，不修改血量、爆头统计或死亡状态；最终结果仍由服务器确认。
    /// </summary>
    private void PredictPvpHitFeedback(Vector3 origin, Vector3 direction, PlayerWeapon weapon)
    {
        PvpPlayerCombat shooter = currentPlayerModel != null
            ? currentPlayerModel.GetComponent<PvpPlayerCombat>()
            : null;
        if (shooter == null || weapon == null || direction.sqrMagnitude <= 0.000001f)
            return;

        Vector3 shotDirection = direction.normalized;
        int hitCount = Physics.RaycastNonAlloc(
            origin,
            shotDirection,
            pvpPredictionHits,
            weapon.range,
            weapon.hitMask,
            QueryTriggerInteraction.Collide);

        Collider nearestSolid = null;
        float nearestSolidDistance = weapon.range;
        Vector3 nearestSolidPoint = origin + shotDirection * weapon.range;
        PvpHeadHitbox nearestHead = null;
        float nearestHeadDistance = weapon.range;
        Vector3 nearestHeadPoint = nearestSolidPoint;

        for (int i = 0; i < hitCount; i++)
        {
            RaycastHit hit = pvpPredictionHits[i];
            if (hit.collider == null || IsLocalPlayerCollider(hit.collider.transform))
                continue;

            PvpHeadHitbox headHitbox = hit.collider.GetComponent<PvpHeadHitbox>();
            if (hit.collider.isTrigger)
            {
                if (headHitbox != null && hit.distance < nearestHeadDistance)
                {
                    nearestHead = headHitbox;
                    nearestHeadDistance = hit.distance;
                    nearestHeadPoint = hit.point;
                }
                continue;
            }

            if (hit.distance < nearestSolidDistance)
            {
                nearestSolid = hit.collider;
                nearestSolidDistance = hit.distance;
                nearestSolidPoint = hit.point;
            }
        }

        PvpPlayerCombat victim = nearestSolid != null
            ? nearestSolid.GetComponentInParent<PvpPlayerCombat>()
            : null;
        bool isHeadshot = false;
        Vector3 hitPoint = nearestSolidPoint;

        if (victim != null)
        {
            for (int i = 0; i < hitCount; i++)
            {
                RaycastHit hit = pvpPredictionHits[i];
                PvpHeadHitbox headHitbox = hit.collider != null
                    ? hit.collider.GetComponent<PvpHeadHitbox>()
                    : null;
                if (headHitbox == null || headHitbox.Owner != victim)
                    continue;

                isHeadshot = true;
                hitPoint = hit.point;
                break;
            }
        }
        else if (nearestHead != null && nearestHeadDistance < nearestSolidDistance)
        {
            victim = nearestHead.Owner;
            isHeadshot = true;
            hitPoint = nearestHeadPoint;
        }

        if (victim == null || victim == shooter || victim.Team == shooter.Team)
            return;

        victim.ShowPredictedHitFeedback(hitPoint, shotDirection, isHeadshot);
    }

    private void TryReload()
    {
        if (currentPlayerModel == null || currentPlayerModel.weapon == null)
            return;

        PlayerWeapon weapon = currentPlayerModel.weapon;
        if (!weapon.CanReload)
            return;

        CancelQueuedAutoReload();
        bool emptyMagazine = weapon.CurrentAmmo <= 0;
        float duration = viewmodel != null
            ? viewmodel.PlayReload(emptyMagazine, weapon.fallbackReloadDuration)
            : weapon.fallbackReloadDuration;
        if (!weapon.BeginReload(emptyMagazine, duration))
            return;

        // 换弹期间允许普通移动，但强制退出开镜/开火/冲刺，且开火不能取消换弹。
        isAiming = false;
        isFire = false;
        isSprint = false;
        ExitAim();
    }

    private void QueueAutoReload(PlayerWeapon weapon)
    {
        if (weapon == null || weapon.IsReloading || weapon.CurrentAmmo > 0)
            return;
        if (autoReloadCoroutine != null && autoReloadWeapon == weapon)
            return;

        CancelQueuedAutoReload();
        autoReloadWeapon = weapon;
        autoReloadCoroutine = StartCoroutine(AutoReloadRoutine(weapon));
    }

    private IEnumerator AutoReloadRoutine(PlayerWeapon weapon)
    {
        yield return new WaitForSeconds(Mathf.Max(0f, weapon.autoReloadDelay));
        autoReloadCoroutine = null;
        autoReloadWeapon = null;

        if (currentPlayerModel != null &&
            currentPlayerModel.weapon == weapon &&
            weapon != null &&
            weapon.CurrentAmmo <= 0 &&
            !weapon.IsReloading)
        {
            TryReload();
        }
    }

    private void CancelQueuedAutoReload()
    {
        if (autoReloadCoroutine != null)
            StopCoroutine(autoReloadCoroutine);
        autoReloadCoroutine = null;
        autoReloadWeapon = null;
    }

    /// <summary>
    /// 屏幕抖动
    /// </summary>
    public void ShakeCamera()
    {
        if (Time.time - lastRecoilShotTime >= recoilPatternResetDelay)
            recoilShotIndex = 0;

        Vector2 patternStep = recoilPattern != null && recoilPattern.Length > 0
            ? recoilPattern[Mathf.Min(recoilShotIndex, recoilPattern.Length - 1)]
            : new Vector2(0f, 0.65f);
        bool usePvpRecoil = IsCurrentPlayerPvp();
        float multiplier = isAiming ? aimingRecoilMultiplier : 1f;
        if (usePvpRecoil && isAiming)
            multiplier *= pvpAimingRecoilMultiplier;
        float verticalMultiplier = usePvpRecoil ? pvpVerticalRecoilMultiplier : 1f;
        float horizontalMultiplier = usePvpRecoil ? pvpHorizontalRecoilMultiplier : 1f;
        float horizontalJitter = usePvpRecoil
            ? Random.Range(-pvpHorizontalJitter, pvpHorizontalJitter)
            : 0f;
        recoilTarget.y = Mathf.Clamp(
            recoilTarget.y + patternStep.y * recoilPitchPerShot * multiplier * verticalMultiplier,
            0f,
            recoilMaxPitch);
        float horizontalDelta =
            (patternStep.x * recoilYawPerShot * horizontalMultiplier + horizontalJitter) * multiplier;
        recoilTarget.x = Mathf.Clamp(
            recoilTarget.x + horizontalDelta,
            -recoilMaxYaw,
            recoilMaxYaw);

        if (usePvpRecoil)
        {
            float side = Mathf.Abs(horizontalDelta) > 0.001f
                ? Mathf.Sign(horizontalDelta)
                : (Random.value < 0.5f ? -1f : 1f);
            cameraRecoilImpulse.x = Mathf.Min(
                cameraRecoilImpulse.x + pvpCameraImpulsePitch * multiplier,
                pvpCameraImpulsePitch * 2.25f);
            cameraRecoilImpulse.y = Mathf.Clamp(
                horizontalDelta * 0.3f + Random.Range(-pvpCameraImpulseYaw, pvpCameraImpulseYaw),
                -pvpCameraImpulseYaw * 1.5f,
                pvpCameraImpulseYaw * 1.5f);
            cameraRecoilImpulse.z = -side * pvpCameraImpulseRoll * multiplier;
            cameraKickbackImpulse = Mathf.Min(
                cameraKickbackImpulse + pvpCameraKickback * multiplier,
                pvpCameraKickback * 2f);
            if (viewmodel != null)
            {
                float horizontalAmount = Mathf.Clamp01(
                    Mathf.Abs(horizontalDelta) / Mathf.Max(0.01f, recoilYawPerShot));
                viewmodel.AddPvpShotRecoil(
                    isAiming,
                    side,
                    horizontalAmount,
                    recoilShotIndex);
            }
        }
        recoilShotIndex++;
        lastRecoilShotTime = Time.time;
        // 当帧立即踢起，后续再由 Update 做平滑累积与回正。
        recoilCurrent = Vector2.Lerp(
            recoilCurrent,
            recoilTarget,
            usePvpRecoil ? 0.78f : 0.35f);

        // 保留旧 Cinemachine Impulse；如果未启用 Brain，手动后坐力仍然会生效。
        if (impulseSource != null)
            impulseSource.GenerateImpulse();
    }

    private void OnEnable()
    {
        input.Enable();
    }

    private void OnDisable()
    {
        CancelQueuedAutoReload();
        input.Disable();
        if (pvpCrosshair != null)
            pvpCrosshair.SetActive(pvpCrosshairInitialActive);
        if (pvpCrosshairPoint != null)
            pvpCrosshairPoint.SetActive(pvpCrosshairPointInitialActive);
        if (viewmodel != null && IsCurrentPlayerPvp())
            viewmodel.SetAiming(false);
        recoilTarget = Vector2.zero;
        recoilCurrent = Vector2.zero;
        cameraRecoilImpulse = Vector3.zero;
        cameraKickbackImpulse = 0f;
        if (viewmodel != null)
            viewmodel.ResetPvpWeaponRecoil();
    }
}
