using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ZombieMoveState : EnemyStateBase
{
    public override void Enter()
    {
        base.Enter();
        enemyModel.PlayStateAnimation("Move");
    }

    public override void Update()
    {
        base.Update();
        if (!enemyModel.IsAtttackTargetInAttackRange())
        {
            enemyModel.ChaseTarget();
        }
        else
        {
            enemyModel.SwitchState(EnemyState.Idle);
        }
    }
}
