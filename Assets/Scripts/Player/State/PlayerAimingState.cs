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
            UpdateAimingTarget();
            playerController.EnterAim();
        }
    }

    public override void Update()
    {
        base.Update();

        if (IsBeControl())
        {
            // 让模型立刻旋转至相机方向
            playerModel.transform.rotation = Quaternion.Euler(0, Camera.main.transform.rotation.eulerAngles.y, 0);
            UpdateAimingTarget();

            #region 待机监听
            if (!playerController.isAiming && !playerController.isFire)
            {
                playerModel.SwitchState(PlayerState.Idle);
                return;
            }
            #endregion

            #region 开火监听
            if (playerController.isFire)
            {
                playerModel.weapon.Fire(playerController.AimTarget.position);
                playerController.ShakeCamera(); // 开火进行屏幕抖动
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

    /// <summary>
    /// 从屏幕中心发射射线确认瞄准位置
    /// </summary>
    private void UpdateAimingTarget()
    {
        // 发射射线
        Ray ray = Camera.main.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0));
        RaycastHit hit;
        
        if (Physics.Raycast(ray, out hit, playerController.maxRayDistance, playerController.aimLayerMask))
            // 更新瞄准目标位置
            playerController.AimTarget.position = hit.point;
        else
            playerController.AimTarget.position = ray.origin + ray.direction * playerController.maxRayDistance;
    }
}
