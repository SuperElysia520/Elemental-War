using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing.Printing;
using UnityEngine;
using UnityEngine.Rendering.Universal.Internal;

public class PlayerWeapnBullet : MonoBehaviour
{
    [Tooltip("伤害")] public int damage = 35;
    [HideInInspector] public Rigidbody rb;
    [Tooltip("推力")] public float flyPower = 700f;
    [Tooltip("存活时间")] public float lifeTime = 8f;

    /// <summary>纯视觉子弹（远端镜像），不参与碰撞与伤害判定</summary>
    [HideInInspector] public bool isCosmetic;

    private Vector3 prePosition; // 记录子弹上一帧的位置

    public void Awake()
    {
        rb = GetComponent<Rigidbody>();
    }

    public void Start()
    {
        rb.velocity = transform.forward * flyPower; // 给子弹一个推力
        Destroy(gameObject, lifeTime);
        
        prePosition = transform.position;
        CheckInitialOverlap();
    }

    private void Update()
    {
        if (isCosmetic)
            return;

        CheckCollision();
        prePosition = transform.position;
    }

    void CheckInitialOverlap()
    {
        if (isCosmetic)
            return;

        Collider[] hitColliders = Physics.OverlapSphere(transform.position, 0.1f);
        foreach (var col in hitColliders)
        {
            EnemyBase enemy = col.GetComponent<EnemyBase>();
            if (enemy != null)
            {
                enemy.Hurt(this, 1);
                Destroy(gameObject);
                return;
            }
        }
    }

    private void CheckCollision()
    {
        RaycastHit hit;
        Vector3 dir = transform.position - prePosition; // 子弹方向
        float distance = Vector3.Distance(transform.position, prePosition);
        
        // 绘制线段检测碰撞
        if (Physics.Raycast(prePosition, dir.normalized, out hit, distance))
        {
            // 检测是否为敌人
            if (hit.collider.CompareTag("Enemy"))
            {
                EnemyBase enemy = hit.collider.GetComponent<EnemyBase>();
                enemy.Hurt(this, 1);
            }
        }
    }
}