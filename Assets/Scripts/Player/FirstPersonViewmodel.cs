using UnityEngine;

/// <summary>
/// 第一人称 viewmodel（手臂+枪）：只做本地渲染与开火视觉，不联网。
/// 由 PlayerController 实例化并挂在本地主相机下。
/// 动画参数与 Low Poly FPS Pack 的 Animator Controller 对齐：Aim/Walk/Run 为 Bool，开火用 "Fire" 状态。
/// </summary>
public class FirstPersonViewmodel : MonoBehaviour
{
    [Tooltip("枪口（子弹生成点）")] public Transform bulletSpawnPoint;
    [Tooltip("枪口火花粒子")] public ParticleSystem muzzleFlash;
    [Tooltip("开火火花粒子")] public ParticleSystem sparkParticles;

    private Animator animator;
    private int aimHash;
    private int walkHash;
    private int runHash;
    private bool aiming;

    void Awake()
    {
        animator = GetComponentInChildren<Animator>();
        aimHash = Animator.StringToHash("Aim");
        walkHash = Animator.StringToHash("Walk");
        runHash = Animator.StringToHash("Run");

        // 安全兜底：禁用子节点里残留的相机，避免出现第二个渲染相机
        foreach (var cam in GetComponentsInChildren<Camera>(true))
            cam.enabled = false;

        // 旧生成预制体可能带有 FPS Pack 示例 Canvas，会把教程文字/弹药 UI 一起显示。
        foreach (var canvas in GetComponentsInChildren<Canvas>(true))
            canvas.gameObject.SetActive(false);

        // 只保留突击步枪、铁瞄和手臂；关闭示例中同时携带的刀/外挂瞄具。
        foreach (var renderer in GetComponentsInChildren<Renderer>(true))
        {
            string objectName = renderer.gameObject.name.ToLowerInvariant();
            if (objectName.Contains("knife") || objectName.StartsWith("scope") || objectName == "silencer")
                renderer.enabled = false;
        }

        // 禁用 FPS Pack 遗留驱动脚本（AutomaticGunScriptLPFP 等）：它们读旧版
        // Input Manager（新输入系统下恒为 false），每帧强制 SetBool(Aim/Walk/Run)
        // 并 anim.Play("Fire")，与本组件抢同一个 Animator，导致开镜/奔跑/开火动画被打断。
        foreach (var mb in GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb == null || mb == this)
                continue;
            if (mb.GetType().Name.EndsWith("LPFP"))
                mb.enabled = false;
        }
    }

    /// <summary>开火动画 + 枪口火花（开镜时播放 Aim Fire，否则播放腰射 Fire）</summary>
    public void Fire()
    {
        if (animator != null)
            animator.Play(aiming ? "Aim Fire" : "Fire", 0, 0f);
        if (muzzleFlash != null)
            muzzleFlash.Emit(1);
        if (sparkParticles != null)
            sparkParticles.Emit(1);
    }

    /// <summary>
    /// 播放空仓/非空仓换弹动画，并返回实际动画片段时长，供弹药逻辑在动画结束时补弹。
    /// </summary>
    public float PlayReload(bool emptyMagazine, float fallbackDuration)
    {
        aiming = false;
        if (animator == null)
            return fallbackDuration;

        animator.SetBool(aimHash, false);
        animator.SetBool(walkHash, false);
        animator.SetBool(runHash, false);
        animator.Play(emptyMagazine ? "Reload Out Of Ammo" : "Reload Ammo Left", 0, 0f);

        string expectedClipName = emptyMagazine ? "reload_out_of_ammo" : "reload_ammo_left";
        RuntimeAnimatorController controller = animator.runtimeAnimatorController;
        if (controller != null)
        {
            foreach (AnimationClip clip in controller.animationClips)
            {
                if (clip != null && clip.name.ToLowerInvariant().Contains(expectedClipName))
                    return clip.length;
            }
        }
        return fallbackDuration;
    }

    public void SetAiming(bool aiming)
    {
        this.aiming = aiming;
        if (animator != null)
            animator.SetBool(aimHash, aiming);
    }

    public void SetMoving(bool moving)
    {
        if (animator != null)
            animator.SetBool(walkHash, moving);
    }

    public void SetRunning(bool running)
    {
        if (animator != null)
            animator.SetBool(runHash, running);
    }
}
