using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 敌人血条
/// </summary>
public class EnemyHealthBarUI : MonoBehaviour
{
    [Tooltip("血条填充块")] public Image healthSlider;
    
    private Transform cameraTransform;

    private void Start()
    {
        cameraTransform = Camera.main.transform;
        healthSlider.fillAmount = 1; // 填满血条
    }

    void update()
    {
        // 计算UI到摄像机的方向
        Vector3 dir = cameraTransform.position - transform.position;
        // 让血条始终面向摄像机
        transform.rotation = Quaternion.LookRotation(-dir); // -dir是因为UI的方向和摄像机的方向正好是相反的
    }

    /// <summary>
    /// 更新血条
    /// </summary>
    /// <param name="healthRatio">血量比例</param>
    public void UpdateHealthBar(float healthRatio)
    {
        healthSlider.fillAmount = healthRatio;
    }
}
