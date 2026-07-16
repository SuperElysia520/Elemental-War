using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 敌人状态基类
/// </summary>
public class EnemyStateBase : StateBase
{
    protected EnemyBase enemyModel;
    
    public override void Init(IStateMachineOwner owner)
    {
        enemyModel = (EnemyBase)owner;
    }

    public override void Enter()
    {
        MonoManager.instance.AddUpdateAction(Update);
    }

    public override void Exit()
    {
        MonoManager.instance.RemoveUpdateAction(Update);
    }

    public override void Destory()
    {
        
    }

    public override void Update()
    {
        
    }

    /// <summary>
    /// 动画是否播放完毕
    /// </summary>
    /// <param name="layer">动画层</param>
    protected bool IsAnimationBreak(int layer)
    {
        AnimatorStateInfo info = enemyModel.animator.GetCurrentAnimatorStateInfo(layer);
        return info.normalizedTime >= 1.0f && !enemyModel.animator.IsInTransition(layer);
    }
}
