using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 躲避鼠标
/// </summary>
public class ExcludeMouse : MonoBehaviour
{
    public float avoidRadius = 10f; // 影响半径
    public float avoidForce = 400f; // 躲避速度
    public float returnForce = 2f; // 返回原位速度

    private Vector2 originalPos; // 引力中心
    private RectTransform rectTransform;
    private Camera uiCamera;

    void Start()
    {
        rectTransform = GetComponent<RectTransform>();
        uiCamera = GetComponentInParent<Canvas>().worldCamera;
        originalPos = rectTransform.anchoredPosition;
    }

    void Update()
    {
        Vector2 mousePositionInRect;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            rectTransform,
            Input.mousePosition,
            uiCamera, out mousePositionInRect
            );
        
        // 指针离按钮的距离
        float distance = mousePositionInRect.magnitude;
        
        Vector2 movement = Vector2.zero;
        if (distance < avoidRadius)
        {
            // 计算躲避方向和力度
            Vector2 avoidDir = -mousePositionInRect.normalized;
            float avoidPercent = (avoidRadius - distance) / avoidRadius;
            movement += avoidDir * avoidForce * avoidPercent * Time.deltaTime;
        }
        
        movement += (originalPos - rectTransform.anchoredPosition) * returnForce * Time.deltaTime;
        
        // 应用移动
        rectTransform.anchoredPosition += movement;
    }
}
