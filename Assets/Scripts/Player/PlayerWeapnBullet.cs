using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class PlayerWeapnBullet : MonoBehaviour
{
    [Tooltip("推力")] public float flyPower = 700f;
    [Tooltip("存活时间")] public float lifeTime = 8f;

    private Rigidbody rb;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
    }

    private void Start()
    {
        // 该预制体只负责曳光视觉；所有伤害都由 PlayerWeapon 的 hitscan 立即结算。
        rb.velocity = transform.forward * flyPower;
        Destroy(gameObject, lifeTime);
    }
}
