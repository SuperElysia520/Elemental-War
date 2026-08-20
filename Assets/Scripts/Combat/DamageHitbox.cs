using UnityEngine;

/// <summary>
/// 可选的命中部位。挂到头部/身体/四肢碰撞体上，用倍率区分伤害。
/// 未挂载时 PlayerWeapon 会自动寻找父级 EnemyBase，按 1 倍身体伤害处理。
/// </summary>
public class DamageHitbox : MonoBehaviour
{
    [Tooltip("所属敌人，留空时自动向父级查找")]
    public EnemyBase enemy;

    [Tooltip("伤害倍率：头部建议 4，身体 1，四肢 0.75")]
    public float damageMultiplier = 1f;

    public EnemyBase Enemy
    {
        get
        {
            if (enemy == null)
                enemy = GetComponentInParent<EnemyBase>();
            return enemy;
        }
    }
}
