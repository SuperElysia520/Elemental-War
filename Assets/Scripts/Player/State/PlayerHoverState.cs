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
        if (!CanUpdate)
            return;

        // 落地优先结算，避免基类先进入 Aiming、随后又被 Hover 强制切回 Idle。
        if (playerModel.cc.isGrounded)
        {
            playerModel.verticalSpeed = playerModel.gravity * Time.deltaTime;
            if (IsBeControl())
            {
                if (playerController.isAiming)
                    playerModel.SwitchState(PlayerState.Aiming);
                else if (playerController.moveInput.sqrMagnitude > 0.0001f)
                    playerModel.SwitchState(PlayerState.Move);
                else
                    playerModel.SwitchState(PlayerState.Idle);
            }
            else
            {
                playerModel.SwitchState(PlayerState.Idle);
            }
            return;
        }

        base.Update();

        if (!CanUpdate || playerModel.CurrentState != PlayerState.Hover)
            return;
    }
}
