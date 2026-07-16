using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 悬空状态
/// </summary>
public class PlayerHoverState : PlayerStateBase
{
    public override void Enter()
    {
        base.Enter();
        playerModel.PlayerStateAnimation("Hover");
    }

    public override void Update()
    {
        base.Update();

        #region 检测角色是否落在地面上
        if (playerModel.cc.isGrounded)
            playerModel.SwitchState(PlayerState.Idle);
        #endregion
    }
}
