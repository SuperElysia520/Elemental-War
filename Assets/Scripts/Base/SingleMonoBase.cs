using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 单例模式限定器
/// T --- 子类
/// </summary>
public class SingleMonoBase<T> : MonoBehaviour where T : SingleMonoBase<T>
{
    public static T instance; // 实例

    protected virtual void Awake()
    {
        if (instance != null)
            Debug.LogError("[SingleMonoBase] instance already exists!");
        instance = (T)this;
    }

    protected virtual void OnDestroy()
    {
        instance = null;
    }
}
