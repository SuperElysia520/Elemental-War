using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 玩家状态基类
/// </summary>
public class PlayerStateBase : StateBase
{
    protected PlayerController playerController;
    protected PlayerModel playerModel; // 当前状态的角色模型
    protected bool CanUpdate => playerModel != null && playerModel.cc != null;
    
    
    public override void Init(IStateMachineOwner owner)
    {
        playerController = PlayerController.instance;
        playerModel = (PlayerModel)owner;
    }
    
    public override void Enter()
    {
        if (MonoManager.instance != null)
            MonoManager.instance.AddUpdateAction(Update);
    }
    
    public override void Exit()
    {
        if (MonoManager.instance != null)
            MonoManager.instance.RemoveUpdateAction(Update);
    }
    
    public override void Destory()
    {
        
    }

    public override void Update()
    {
        // 状态机经由 MonoManager 委托驱动，退出游戏时 playerModel/cc 可能已被销毁
        if (!CanUpdate)
            return;

        #region 重力计算
        if (!playerModel.cc.isGrounded)
        {
            playerModel.verticalSpeed += playerModel.gravity * Time.deltaTime; // 施加重力
            if (playerModel.IsHover())
                playerModel.SwitchState(PlayerState.Hover);   
        }
        else
            playerModel.verticalSpeed = playerModel.gravity * Time.deltaTime; // 重置重力速度
        #endregion

        #region 瞄准状态监听
        // 瞄准状态在待机或者运动状态都可以进行瞄准所以写在状态基类
        // 只有右键开镜才进入 Aiming；左键腰射由 PlayerController 独立处理。
        if (IsBeControl() && playerController.isAiming)
        {
            playerModel.SwitchState(PlayerState.Aiming);
        }
        #endregion
    }
    
    public bool IsBeControl()
    {
        return playerController != null && playerModel == playerController.currentPlayerModel;
    }

    /// <summary>
    /// 切换到跳跃状态
    /// </summary>
    public void SwitchToHover()
    {
        // 计算跳跃力度
        playerModel.verticalSpeed = Mathf.Sqrt(-2  * playerModel.gravity * playerModel.jumpHeight);
        // 切换到悬空状态
        playerModel.SwitchState(PlayerState.Hover);
    }
}
