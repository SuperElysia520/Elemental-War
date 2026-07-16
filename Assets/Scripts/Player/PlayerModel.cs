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
    
    private void Awake()
    {
        stateMachine = new StateMachine(this);
        animator = GetComponent<Animator>();
        cc = GetComponent<CharacterController>();
        navMeshAgent = GetComponent<NavMeshAgent>();
        navMeshAgent.stoppingDistance = stoppingDistance;
        navMeshAgent.angularSpeed = PlayerController.instance.rotationSpeed;
    }

    void Start()
    {
        SwitchState(PlayerState.Idle); // 默认切换待机状态
        ExitAim();
    }

    void Update()
    {
        
    }
    
    /// <summary>
    /// 进入模型
    /// </summary>
    public void Enter()
    {
        navMeshAgent.enabled = false;
    }

    /// <summary>
    /// 退出模型
    /// </summary>
    public void Exit()
    {
        navMeshAgent.enabled = true;
        SwitchState(PlayerState.Idle);
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
        Vector3 playerDeltaMovement = animator.deltaPosition; // 获取的动画控制器当前帧的位置信息
        if (currentState != PlayerState.Hover)
            UpdateAverageCacheSpeed(animator.velocity);
        else
        {
            playerDeltaMovement = averageDeltamovement * Time.deltaTime;
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
