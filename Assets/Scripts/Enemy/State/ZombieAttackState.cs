using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ZombieAttackState : EnemyStateBase
{
    public override void Enter()
    {
        base.Enter();
        enemyModel.PlayStateAnimation("Attack");
    }
}
