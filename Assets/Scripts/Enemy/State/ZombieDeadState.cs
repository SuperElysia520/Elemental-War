using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 死亡状态
/// </summary>
public class ZombieDeadState : EnemyStateBase
{
    public override void Enter()
    {
        base.Enter();
        enemyModel.PlayStateAnimation("Dead");
    }

    public override void Update()
    {
        base.Update();
        if (IsAnimationBreak(0))
        {
            enemyModel.Clear();
        }
    }
}
