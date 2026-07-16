using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ZombieIdleState : EnemyStateBase
{
    public override void Enter()
    {
        base.Enter();
        enemyModel.PlayStateAnimation("Idle");
        enemyModel.navMeshAgent.velocity = Vector3.zero;
    }

    public override void Update()
    {
        base.Update();
        if (!enemyModel.IsAtttackTargetInAttackRange())
        {
            enemyModel.SwitchState(EnemyState.Move);
        }
    }
}
