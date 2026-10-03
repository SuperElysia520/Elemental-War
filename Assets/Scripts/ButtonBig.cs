using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

public class ButtonBig : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    private float hoverScale = 1.1f; // 悬停放大倍数
    
    private Vector3 originalScale;
    
    void Start()
    {
        originalScale = transform.localScale;
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        transform.localScale = originalScale * hoverScale;
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        transform.localScale = originalScale;
    }
}
