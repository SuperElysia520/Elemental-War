using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

/// <summary>
/// 任务处理管理器
/// </summary>
public class MonoManager : SingleMonoBase<MonoManager>
{
    private Action updateAction; // 任务集合

    /// <summary>
    /// 添加任务
    /// </summary>
    /// <param name="task">事件</param>
    public void AddUpdateAction(Action task)
    {
        updateAction += task;
    }

    /// <summary>
    /// 移除任务
    /// </summary>
    /// <param name="task">事件</param>
    public void RemoveUpdateAction(Action task)
    {
        updateAction -= task;
    }
    
    void Start()
    {
        
    }
    
    void Update()
    {
        updateAction?.Invoke();
    }
}
