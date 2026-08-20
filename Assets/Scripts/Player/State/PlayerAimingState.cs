using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 瞄准状态
/// </summary>
public class PlayerAimingState : PlayerStateBase
{
    #region 动画器相关
    private int aimingXHash;
    private int aimingYHash;
    private float aimingX = 0;
    private float aimingY = 0;
    private float transitionSpeed = 5; // 过渡时间
    #endregion

    public override void Init(IStateMachineOwner owner)
    {
        base.Init(owner);
        aimingXHash = Animator.StringToHash("AimingX");
        aimingYHash = Animator.StringToHash("AimingY");
    }

    public override void Enter()
    {
        base.Enter();
        playerModel.PlayerStateAnimation("Aiming");
        if (IsBeControl())
        {
            playerController.EnterAim();
        }
    }

    public override void Update()
    {
        if (!CanUpdate)
            return;

        base.Update();

        if (!CanUpdate)
            return;

        if (IsBeControl())
        {
            // 第一人称：身体 yaw 由 PlayerController 直接设到相机朝向，这里不再回读相机旋转

            #region 跳跃监听
            if (playerController.isJumping)
            {
                SwitchToHover();
                return;
            }
            #endregion

            #region 待机监听
            if (!playerController.isAiming)
            {
                playerModel.SwitchState(playerController.moveInput.sqrMagnitude > 0.0001f
                    ? PlayerState.Move
                    : PlayerState.Idle);
                return;
            }
            #endregion

            #region 处理移动输入
            aimingX = Mathf.Lerp(aimingX, playerController.moveInput.x, transitionSpeed * Time.deltaTime);
            aimingY = Mathf.Lerp(aimingY, playerController.moveInput.y, transitionSpeed * Time.deltaTime);
            playerModel.animator.SetFloat(aimingXHash, aimingX);
            playerModel.animator.SetFloat(aimingYHash, aimingY);
            #endregion
        }
        // 人机模式
        else
        {
            Debug.Log("Aiming");
        }
    }

    public override void Exit()
    {
        base.Exit();
        if (IsBeControl())
            playerController.ExitAim();
    }
}
