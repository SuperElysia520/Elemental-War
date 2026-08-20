using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;

/// <summary>
/// 移动状态
/// </summary>
public class PlayerMoveState : PlayerStateBase
{
    #region 动画器相关
    private int moveBlendHash; // 属性 --- 将moveBlend字符串转化为哈希值，就不用字符串查找了
    private float moveBlend; // 参数
    private float runThreshold = 0; // 奔跑阈值
    private float sprintThreshold = 1; // 冲刺阈值
    private float transitionSpeed = 5; // 过渡速度
    #endregion

    // 在Unity中，为了提高性能，通常不会直接使用字符串（如"MoveBlend"）来频繁调用SetFloat，
    // 而是先通过Animator.StringToHash("参数名")将参数名称转换成一个整数哈希值，
    // 然后使用这个哈希值来引用参数。这样避免了每帧的字符串哈希计算开销
    public override void Init(IStateMachineOwner owner)
    {
        base.Init(owner);
        moveBlendHash = Animator.StringToHash("MoveBlend"); // 字符串转化为哈希值
    }
    
    public override void Enter()
    {
        base.Enter();
        playerModel.PlayerStateAnimation("Move");
    }

    public override void Update()
    {
        if (!CanUpdate)
            return;

        base.Update();

        if (!CanUpdate)
            return;

        // 检测是否被玩家控制
        if (IsBeControl())
        {
            #region 跳跃状态监听
            if (playerController.isJumping)
            {
                SwitchToHover();
                return;
            }
            #endregion
            
            #region 待机状态监听
            if (playerController.moveInput.magnitude == 0)
            {
                playerModel.SwitchState(PlayerState.Idle);
                return;
            }
            #endregion

            #region 处理移动速度
            if (playerController.isSprint)
                moveBlend = Mathf.Lerp(moveBlend, sprintThreshold, Time.deltaTime * transitionSpeed);
            else
                moveBlend = Mathf.Lerp(moveBlend, runThreshold, Time.deltaTime * transitionSpeed);
            playerModel.animator.SetFloat(moveBlendHash, moveBlend); // 将浮点数传递给动画控制器，第一个参数用于查找，第二个参数用于传递的值
            #endregion

            // 第一人称下身体朝向已由 PlayerController 直接设为相机 yaw，这里不再旋转模型
        }
        // 人机模式
        else
        {
            #region 处理移动速度
            if (playerModel.DistanceOfCurrentPlayerModel() - playerModel.stoppingDistance < 2f)
            {
                moveBlend = Mathf.Lerp(moveBlend, runThreshold, transitionSpeed * Time.deltaTime);
            }
            else
            {
                moveBlend = Mathf.Lerp(moveBlend, sprintThreshold, transitionSpeed * Time.deltaTime);
            }
            playerModel.animator.SetFloat(moveBlendHash, moveBlend);
            #endregion
            
            #region 自动跟随玩家
            if (playerModel.DistanceOfCurrentPlayerModel() <= playerModel.stoppingDistance)
            {
                playerModel.SwitchState(PlayerState.Idle);
                return;
            }
            playerModel.navMeshAgent.SetDestination(playerController.currentPlayerModel.transform.position);
            #endregion
        }
    }
}
