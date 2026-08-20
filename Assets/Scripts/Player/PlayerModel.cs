using System.Collections;
using System.Collections.Generic;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Animations.Rigging;

public enum PlayerState
{
    Idle,
    Move,
    Hover,
    Aiming,
}
/// <summary>
/// 角色模型
/// </summary>
public class PlayerModel : MonoBehaviour, IStateMachineOwner
{
    [Tooltip("角色武器")] public PlayerWeapon weapon;
    
    [HideInInspector] public Animator animator;
    [HideInInspector] public CharacterController cc; // 在游戏开始前自动添加
    private StateMachine stateMachine; // 动画状态机
    private PlayerState currentState; // 当前状态

    /// <summary>当前动画状态（联机同步壳读取用）</summary>
    public PlayerState CurrentState => currentState;

    #region 约束相关
    public TwoBoneIKConstraint rightHandConstraint; // 正常状态下的右手约束
    public MultiAimConstraint rightHandAimConstraint; // 瞄准状态下的右手约束
    public MultiAimConstraint bodyAimConstraint; // 身躯约束
    #endregion

    #region 垂直速度相关
    [Tooltip("重力")] public float gravity = -15;
    [Tooltip("跳跃高度")] public float jumpHeight = 1.5f;
    [HideInInspector] public float verticalSpeed; // 当前垂直方向速度
    [Tooltip("悬空判定高度")] public float fallHeight = 0.2f;
    #endregion

    #region 玩家在地面时前三帧速度的缓存
    private static readonly int CACHE_SIZE = 3;
    Vector3[] speedCache = new Vector3[CACHE_SIZE]; // 动画前三帧的玩家速度
    private int speedCacheIndex = 0; // 缓存保存的位置
    private Vector3 averageDeltamovement; // 平均速度
    #endregion

    #region 人机相关
    [HideInInspector] public NavMeshAgent navMeshAgent;
    public float stoppingDistance = 2f; // 停止跟随距离
    #endregion

    #region 第一人称代码驱动移动
    [Tooltip("是否由外部速度驱动水平移动（受控玩家为 true，人机为 false）")]
    [HideInInspector] public bool useExternalMove;
    [Tooltip("外部水平速度（世界空间，受控玩家由 PlayerController 计算）")]
    [HideInInspector] public Vector3 externalMoveVelocity;
    [Tooltip("步行速度")] public float moveSpeed = 5f;
    [Tooltip("冲刺速度")] public float sprintSpeed = 8f;
    #endregion

    private RigBuilder rigBuilder;

    private void Awake()
    {
        stateMachine = new StateMachine(this);
        animator = GetComponent<Animator>();
        cc = GetComponent<CharacterController>();
        navMeshAgent = GetComponent<NavMeshAgent>();
        navMeshAgent.stoppingDistance = stoppingDistance;
        navMeshAgent.angularSpeed = PlayerController.instance.rotationSpeed;
        rigBuilder = GetComponentInChildren<RigBuilder>();
    }

    void Start()
    {
        SwitchState(PlayerState.Idle); // 默认切换待机状态
        ExitAim();
    }

    void Update()
    {
        
    }

    private void OnDestroy()
    {
        stateMachine?.Stop();
        if (PlayerController.instance != null && PlayerController.instance.currentPlayerModel == this)
            PlayerController.instance.currentPlayerModel = null;
    }
    
    /// <summary>
    /// 进入模型
    /// </summary>
    public void Enter()
    {
        navMeshAgent.enabled = false;
        useExternalMove = true;
    }

    /// <summary>
    /// 退出模型
    /// </summary>
    public void Exit()
    {
        navMeshAgent.enabled = true;
        useExternalMove = false;
        externalMoveVelocity = Vector3.zero;
        SwitchState(PlayerState.Idle);
    }

    /// <summary>
    /// 停止状态机与本地驱动（远端联机镜像用：位置由网络同步，动画由网络壳镜像）
    /// </summary>
    public void Stop()
    {
        stateMachine.Stop();
    }

    /// <summary>
    /// 把瞄准约束的 Source Object 重新指向准星目标。
    /// 单机场景里这两个 MultiAim 约束的 source object 是空的，靠场景实例 override 指向 AimTarget；
    /// 联机角色是从预制体新实例化的，override 不生效，必须运行时补上，否则手部/身躯不会朝准星瞄准。
    /// </summary>
    public void BindAimTargetToConstraints(Transform aimTarget)
    {
        if (aimTarget == null)
            return;

        if (rightHandAimConstraint != null)
        {
            ref var data = ref rightHandAimConstraint.data;
            var sources = data.sourceObjects;
            if (sources.Count > 0)
            {
                sources.SetTransform(0, aimTarget);
                data.sourceObjects = sources;
            }
        }

        if (bodyAimConstraint != null)
        {
            ref var data = ref bodyAimConstraint.data;
            var sources = data.sourceObjects;
            if (sources.Count > 0)
            {
                sources.SetTransform(0, aimTarget);
                data.sourceObjects = sources;
            }
        }

        // 关键：约束在 RigBuilder.Build 时会被 IsValid() 过滤，source object 为空时直接丢弃，
        // 之后即使改了 data 也不会被 RigLayer 收集。补绑后必须重新 Build，让约束重新进入 job 系统。
        if (rigBuilder != null && Application.isPlaying)
            rigBuilder.Build();
    }

    /// <summary>
    /// 切换状态
    /// </summary>
    public void SwitchState(PlayerState state)
    {
        switch (state)
        {
            case PlayerState.Idle:
                stateMachine.EnterState<PlayerIdleState>();
                break;
            case PlayerState.Move:
                stateMachine.EnterState<PlayerMoveState>();
                break;
            case PlayerState.Hover:
                stateMachine.EnterState<PlayerHoverState>();
                break;
            case PlayerState.Aiming:
                stateMachine.EnterState<PlayerAimingState>();
                break;
        }
        currentState = state; // 每次交换完状态记录当前状态
    }

    /// <summary>
    /// 播放动画
    /// </summary>
    /// <param name="animationName">动画名称</param>
    /// <param name="transition">过渡时间</param>
    /// <param name="layer">动画层</param>
    public void PlayerStateAnimation(string animationName, float transition = 0.25f, int layer = 0)
    {
        animator.CrossFadeInFixedTime(animationName, transition, layer); // 控制播放动画平滑淡出，目标动画平滑淡入
    }

    /// <summary>
    /// 判断是否悬空
    /// </summary>
    /// <returns></returns>
    public bool IsHover()
    {
        return !Physics.Raycast(transform.position, Vector3.down, fallHeight);
    }

    /// <summary>
    /// 计算模型前三帧的平均速度
    /// </summary>
    /// <param name="newSpeed">当前速度</param>
    private void UpdateAverageCacheSpeed(Vector3 newSpeed)
    {
        speedCache[speedCacheIndex++] = newSpeed;
        speedCacheIndex %= CACHE_SIZE; // 防止出现数组越界
        // 计算缓存池中的平均速度
        Vector3 sum = Vector3.zero;
        foreach (Vector3 cache in speedCache)
            sum += cache;
        averageDeltamovement = sum / CACHE_SIZE;
    }

    public void OnAnimatorMove()
    {
        if (cc == null || !cc.enabled)
            return;

        Vector3 playerDeltaMovement;
        if (useExternalMove)
        {
            // 第一人称：水平由外部速度（PlayerController/状态机）驱动，垂直由重力/跳跃驱动
            playerDeltaMovement = externalMoveVelocity * Time.deltaTime;
        }
        else
        {
            playerDeltaMovement = animator.deltaPosition; // 获取的动画控制器当前帧的位置信息
            if (currentState != PlayerState.Hover)
                UpdateAverageCacheSpeed(animator.velocity);
            else
            {
                playerDeltaMovement = averageDeltamovement * Time.deltaTime;
            }
        }
        playerDeltaMovement.y = verticalSpeed * Time.deltaTime;
        cc.Move(playerDeltaMovement);
    }

    /// <summary>
    /// 进入瞄准
    /// </summary>
    public void EnterAim()
    {
        // 启动瞄准约束
        rightHandAimConstraint.weight = 1;
        bodyAimConstraint.weight = 1;
        rightHandConstraint.weight = 0;
    }

    /// <summary>
    /// 退出瞄准
    /// </summary>
    public void ExitAim()
    {
        // 关闭瞄准约束
        rightHandAimConstraint.weight = 0;
        bodyAimConstraint.weight = 0;
        rightHandConstraint.weight = 1;
    }

    /// <summary>
    /// 计算该模型与玩家控制模型之间的距离
    /// </summary>
    public float DistanceOfCurrentPlayerModel()
    {
        return Vector3.Distance(transform.position, PlayerController.instance.currentPlayerModel.transform.position);
    }
}
