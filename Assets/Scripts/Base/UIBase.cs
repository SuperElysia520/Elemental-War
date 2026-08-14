using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

// UI基类
public abstract class UIBase<T> : SingleMonoBase<T> where T : UIBase<T>
{
    public bool show;
    private Animator animator;

    protected virtual void Awake()
    {
        base.Awake();
        animator = GetComponent<Animator>();
    }

    protected virtual void Start()
    {
        gameObject.SetActive(show);
    }

    /// <summary>
    /// 显示UI
    /// </summary>
    public virtual void Enter()
    {
        gameObject.SetActive(true);
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        StartCoroutine(_Enter());
    }

    private IEnumerator _Enter()
    {
        gameObject.SetActive(true);
        PlayAnimation("FadeIn");
        yield return new WaitForSeconds(0.01f);
        yield return new WaitUntil(() => IsAnimationBreak());
        animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
        ResumeButtons();
    }

    /// <summary>
    /// 隐藏UI
    /// </summary>
    public virtual void Exit(Action action)
    {
        DisableButtons();
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        StartCoroutine(_Exit(action));
    }

    private IEnumerator _Exit(Action action)
    {
        PlayAnimation("FadeOut");
        yield return new WaitForSeconds(0.05f);
        action?.Invoke();
    }

    /// <summary>
    /// 播放动画
    /// </summary>
    /// <param name="animationName">动画名称</param>
    /// <param name="transition">过渡时间</param>
    /// <param name="layer">动画层</param>
    public void PlayAnimation(string animationName)
    {
        animator.CrossFadeInFixedTime(animationName, 0); // 控制播放动画平滑淡出，目标动画平滑淡入
    }

    /// <summary>
    /// 动画是否播放完毕
    /// </summary>
    /// <param name="layer">动画层</param>
    protected bool IsAnimationBreak()
    {
        AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
        return info.normalizedTime >= 1.0f && !animator.IsInTransition(0);
    }

    /// <summary>
    /// 禁用所有按钮
    /// </summary>
    protected abstract void DisableButtons();

    /// <summary>
    /// 恢复所有按钮
    /// </summary>
    protected abstract void ResumeButtons();
    
    public IEnumerator DisplayBtnExit()
    {
        yield return new WaitForSeconds(0.65f);
        MainMenuUI.instance.btnExit.gameObject.SetActive(true);
    }
}
