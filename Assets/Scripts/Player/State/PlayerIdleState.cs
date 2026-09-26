using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 玩家待机状态
/// </summary>
public class PlayerIdleState : PlayerStateBase
{
    public override void Enter()
    {
        base.Enter(); // 调用父类的Enter方法
        playerModel.PlayerStateAnimation("Idle"); // 播放idle动画
    }

    public override void Update()
    {
        if (!CanUpdate || playerModel.CurrentState != PlayerState.Idle)
            return;

        base.Update();

        if (!CanUpdate || playerModel.CurrentState != PlayerState.Idle)
            return;

        // 检测是否被玩家控制
        if (IsBeControl())
        {
            #region 移动状态监听
            if (playerController.moveInput.magnitude != 0)
                playerModel.SwitchState(PlayerState.Move);
            #endregion

            #region 跳跃状态监听
            if (playerController.isJumping)
                SwitchToHover();
            #endregion
        }
        // 人机模式
        else
        {
            if (playerModel.DistanceOfCurrentPlayerModel() > playerModel.stoppingDistance)
            {
                playerModel.SwitchState(PlayerState.Move);
            }
        }
    }
}
