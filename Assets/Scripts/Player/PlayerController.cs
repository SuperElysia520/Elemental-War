using System.Collections;
using System.Collections.Generic;
using Cinemachine;
using UnityEngine;
using UnityEngine.Animations.Rigging;

/// <summary>
/// 玩家控制器
/// </summary>
public class PlayerController : SingleMonoBase<PlayerController>
{
    public PlayerModel currentPlayerModel; //当前操控的角色模型
    private Transform cameraTransform;

    [Tooltip("正常视角相机")] public CinemachineFreeLook freeLookCamera;
    [Tooltip("瞄准视角相机")] public CinemachineFreeLook aimingCamera;
    
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
    
    [HideInInspector] public Vector3 localMovement; // 本地空间下的玩家移动方向
    [HideInInspector] public Vector3 worldMovement; // 世界空间下的玩家移动方向

    protected override void Awake()
    {
        base.Awake();
        input = new MyInputSystem();
    }

    void Start()
    {
        cameraTransform = Camera.main.transform;
        Cursor.lockState = CursorLockMode.Locked; // 锁定光标
        ExitAim();
        impulseSource = aimingCamera.GetComponent<CinemachineImpulseSource>();
        ResetCameraTarget();
    }

    void Update()
    {
        #region 更新玩家输入
        moveInput = input.Player.Move.ReadValue<Vector2>().normalized;
        isSprint = input.Player.IsSprint.IsPressed();
        isAiming = input.Player.IsAiming.IsPressed();
        isJumping = input.Player.IsJumping.triggered;
        isFire = input.Player.Fire.IsPressed();
        #endregion

        #region 计算玩家移动方向
        // 获取相机的方向向量
        Vector3 cameraForwardProjection = new Vector3(cameraTransform.forward.x, 0, cameraTransform.forward.z).normalized;
        // 计算世界空间下的方向向量
        worldMovement = cameraForwardProjection * moveInput.y + cameraTransform.right * moveInput.x;
        // 将世界空间下的方向向量转换为模型本地空间下的方向向量
        localMovement = currentPlayerModel.transform.InverseTransformDirection(worldMovement);
        #endregion

        #region 切换角色输入监听

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
        ResetCameraTarget();
    }

    /// <summary>
    /// 进入瞄准
    /// </summary>
    public void EnterAim()
    {
        // 同步瞄准相机和自由相机的旋转角度
        aimingCamera.m_XAxis.Value = freeLookCamera.m_XAxis.Value;
        aimingCamera.m_YAxis.Value = freeLookCamera.m_YAxis.Value;
        
        currentPlayerModel.EnterAim();
        
        // 设置相机的优先级，使瞄准相机生效
        freeLookCamera.Priority = 0;
        aimingCamera.Priority = 100;
    }

    /// <summary>
    /// 退出瞄准
    /// </summary>
    public void ExitAim()
    {
        // 同步自由相机和瞄准相机的旋转角度
        freeLookCamera.m_XAxis.Value = aimingCamera.m_XAxis.Value;
        freeLookCamera.m_YAxis.Value = aimingCamera.m_YAxis.Value;
        
        currentPlayerModel.ExitAim();
        
        // 设置相机的优先级，使自由相机生效
        freeLookCamera.Priority = 100;
        aimingCamera.Priority = 0;
    }

    /// <summary>
    /// 重置摄像机瞄准目标
    /// </summary>
    public void ResetCameraTarget()
    {
        aimingCamera.Follow = currentPlayerModel.transform;
        aimingCamera.LookAt = currentPlayerModel.transform;
        freeLookCamera.Follow = currentPlayerModel.transform;
        freeLookCamera.LookAt = currentPlayerModel.transform;
    }

    /// <summary>
    /// 屏幕抖动
    /// </summary>
    public void ShakeCamera()
    {
        impulseSource.GenerateImpulse();
    }

    private void OnEnable()
    {
        input.Enable();
    }

    private void OnDisable()
    {
        input.Disable();
    }
}
