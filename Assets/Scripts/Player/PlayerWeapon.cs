using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PlayerWeapon : MonoBehaviour
{
    [Tooltip("子弹生成位置")] public Transform bulletSpawnPoint;
    [Tooltip("子弹预制体")] public PlayerWeapnBullet bulletEffectPrefab;
    [Tooltip("枪管火花预制体")] public GameObject bulletSparkPrefab;
    [Tooltip("子弹发射间隔")] public float bulletInterval = 0.15f;
    private float lastFireTime; // 上一次子弹发射时间
    
    /// <summary>
    /// 朝着targetPos方向发射子弹
    /// </summary>
    /// <param name="targetPos"></param>
    public void Fire(Vector3 targetPos)
    {
        // 检查发射间隔
        if (Time.time - lastFireTime < bulletInterval)
            return;
        lastFireTime = Time.time;
        // 计算发射方向
        Vector3 direction = targetPos - bulletSpawnPoint.position;
        direction.Normalize();
        // 实例化子弹预制体
        PlayerWeapnBullet bulletEffect = Instantiate(bulletEffectPrefab, bulletSpawnPoint.position, Quaternion.identity);
        bulletEffect.transform.forward = direction; // 设置子弹朝向
        // 实例化火花预制体
        GameObject spark = Instantiate(bulletSparkPrefab, bulletSpawnPoint.position, Quaternion.identity);
        spark.transform.forward = direction; // 设置火花朝向
    }
}
