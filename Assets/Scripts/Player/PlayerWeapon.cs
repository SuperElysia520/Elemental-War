using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PlayerWeapon : MonoBehaviour
{
    #region 基础属性
    [Header("基础属性")]
    [Tooltip("子弹生成位置")] public Transform bulletSpawnPoint;
    [Tooltip("子弹预制体")] public PlayerWeapnBullet bulletEffectPrefab;
    [Tooltip("枪管火花预制体")] public GameObject bulletSparkPrefab;
    [Tooltip("子弹发射间隔")] public float bulletInterval = 0.1f; // 每秒射速 = 1 / 发射间隔
    [Tooltip("弹匣容量")] public int magazineSize = 30;
    [Tooltip("打空弹匣后，等待多久开始自动换弹")] public float autoReloadDelay = 0.1f;
    [Tooltip("找不到第一人称换弹动画时使用的兜底时长")] public float fallbackReloadDuration = 3f;
    [Tooltip("单发基础伤害")] public float damage = 34f;
    [Tooltip("射线最大射程")] public float range = 250f;
    [Tooltip("射线可命中的层级")] public LayerMask hitMask = ~0;
    [Tooltip("头部伤害倍率")] public float headDamageMultiplier = 4f;
    [Tooltip("下肢伤害倍率")] public float limbDamageMultiplier = 0.75f;
    [Range(0.5f, 1f)]
    [Tooltip("未配置 DamageHitbox 时，命中高度超过此比例视为头部")]
    public float fallbackHeadHeight = 0.78f;
    [Range(0f, 0.5f)]
    [Tooltip("未配置 DamageHitbox 时，命中高度低于此比例视为下肢")]
    public float fallbackLimbHeight = 0.32f;
    #endregion

    #region 枪械音效
    [Header("枪械音效")]
    [Tooltip("每颗真实子弹都播放一次的单发音效")] public AudioClip singleShotSound;
    [Tooltip("连续射击时低音量叠加的连发压枪音效")] public AudioClip sustainedFireSound;
    [Tooltip("弹匣全空时的换弹音效")] public AudioClip emptyReloadSound;
    [Tooltip("弹匣还有子弹时的换弹音效")] public AudioClip partialReloadSound;
    [Range(0f, 1f)] public float singleShotVolume = 1f;
    [Range(0f, 1f)] public float sustainedFireVolume = 0.18f;
    [Tooltip("连续第几发开始淡入压枪辅助层")] public int sustainedFireStartShot = 3;
    [Tooltip("压枪辅助层的淡入淡出速度")] public float soundFadeSpeed = 8f;
    [Tooltip("远端 3D 音效最小距离")] public float remoteMinDistance = 4f;
    [Tooltip("远端 3D 音效最大距离")] public float remoteMaxDistance = 60f;
    #endregion

    #region 精度与散布
    [Header("精度与散布（角度）")]
    [Tooltip("静止第一发散布")] public float baseSpread = 0.08f;
    [Tooltip("普通移动附加散布")] public float moveSpreadPenalty = 0.85f;
    [Tooltip("奔跑附加散布")] public float sprintSpreadPenalty = 2.5f;
    [Tooltip("空中附加散布")] public float airSpreadPenalty = 4f;
    [Tooltip("连射每发增加的散布")] public float spreadPerShot = 0.11f;
    [Tooltip("连射散布上限")] public float maxSpraySpread = 1.5f;
    [Range(0.1f, 1f)]
    [Tooltip("开镜时的散布倍率")] public float aimingSpreadMultiplier = 0.55f;
    #endregion

    private float lastFireTime = -999f; // 上一次子弹发射时间
    private PlayerModel ownerModel;
    private readonly RaycastHit[] hitBuffer = new RaycastHit[32];
    private Coroutine reloadCoroutine;
    private AudioSource localOneShotSource;
    private AudioSource localSustainedSource;
    private AudioSource remoteOneShotSource;
    private AudioSource remoteSustainedSource;
    private float localLastSoundShotTime = -999f;
    private float remoteLastSoundShotTime = -999f;
    private int localSoundShotStreak;
    private int remoteSoundShotStreak;
    private bool localSustainedWanted;
    private bool remoteSustainedWanted;

    public int CurrentAmmo { get; private set; }
    public bool IsReloading { get; private set; }
    public bool CanReload => !IsReloading && CurrentAmmo < Mathf.Max(1, magazineSize);

    /// <summary>最近一次射击的真实射线起点，供 PVP 服务器复核命中。</summary>
    public Vector3 LastShotOrigin { get; private set; }

    /// <summary>最近一次真实射线的终点，供本地与远端曳光从枪口汇聚到同一落点。</summary>
    public Vector3 LastShotVisualTarget { get; private set; }

    /// <summary>实际发射时触发（联机同步壳订阅后广播给远端）。参数：子弹生成位置、发射方向。</summary>
    public event Action<Vector3, Vector3> onFire;

    /// <summary>本地开始换弹时触发。参数为是否空仓换弹，联机同步壳据此广播远端音效。</summary>
    public event Action<bool> onReloadStarted;

    private void Awake()
    {
        magazineSize = Mathf.Max(1, magazineSize);
        CurrentAmmo = magazineSize;
        sustainedFireStartShot = Mathf.Max(2, sustainedFireStartShot);
        CreateAudioSources();
    }

    private void Update()
    {
        float burstTimeout = Mathf.Max(0.08f, bulletInterval * 1.75f);
        if (Time.time - localLastSoundShotTime > burstTimeout)
        {
            localSoundShotStreak = 0;
            localSustainedWanted = false;
        }
        if (Time.time - remoteLastSoundShotTime > burstTimeout)
        {
            remoteSoundShotStreak = 0;
            remoteSustainedWanted = false;
        }

        UpdateSustainedSource(localSustainedSource, localSustainedWanted);
        UpdateSustainedSource(remoteSustainedSource, remoteSustainedWanted);
    }

    /// <summary>
    /// 从指定原点朝指定方向发射子弹（第一人称用相机朝向）。
    /// </summary>
    /// <param name="origin">子弹生成原点（第一人称取 viewmodel 枪口/相机）</param>
    /// <param name="direction">发射方向（世界空间）</param>
    /// <returns>本次是否真正发射（受发射间隔限制）。调用方据此决定是否播放开火视觉/抖动。</returns>
    public bool Fire(Vector3 origin, Vector3 direction, float spreadDegrees = 0f)
    {
        return Fire(origin, direction, origin, spreadDegrees);
    }

    /// <summary>
    /// 命中射线和视觉曳光使用不同起点。PVP 的真实判定从相机准星射线出发，
    /// 第一人称曳光仍从 viewmodel 枪口生成，避免贴脸时枪口越过目标命中面。
    /// </summary>
    public bool Fire(
        Vector3 hitOrigin,
        Vector3 hitDirection,
        Vector3 visualOrigin,
        float spreadDegrees)
    {
        return Fire(hitOrigin, hitDirection, visualOrigin, spreadDegrees, false);
    }

    /// <summary>
    /// PVP 可让视觉曳光从枪口汇聚到真实准心射线的终点；伤害仍从相机中心射线判定，
    /// 避免近距离因为相机与枪口存在视差而出现“准心命中、曳光从旁边飞过”。
    /// </summary>
    public bool Fire(
        Vector3 hitOrigin,
        Vector3 hitDirection,
        Vector3 visualOrigin,
        float spreadDegrees,
        bool convergeVisualToHitPoint)
    {
        if (IsReloading || CurrentAmmo <= 0)
            return false;

        // 检查发射间隔
        if (Time.time - lastFireTime < bulletInterval)
            return false;
        lastFireTime = Time.time;
        CurrentAmmo--;
        PlayLocalShotSound();
        if (CurrentAmmo <= 0)
            StopLocalSustainedFire();

        Vector3 direction = ApplySpread(hitDirection.normalized, spreadDegrees);
        LastShotOrigin = hitOrigin;

        LastShotVisualTarget = PerformHitscan(hitOrigin, direction);
        Vector3 visualDirection = direction;
        if (convergeVisualToHitPoint)
        {
            Vector3 toHitPoint = LastShotVisualTarget - visualOrigin;
            if (toHitPoint.sqrMagnitude > 0.000001f)
                visualDirection = toHitPoint.normalized;
        }

        // 真实伤害已由射线立即判定，子弹预制体只作为曳光视觉。
        SpawnVisual(visualOrigin, visualDirection);
        // 广播给远端时用第三人称枪口位置，让远端在自己屏幕上看子弹从该角色枪口飞出
        onFire?.Invoke(bulletSpawnPoint != null ? bulletSpawnPoint.position : visualOrigin, direction);
        return true;
    }

    /// <summary>
    /// 开始一次不可被开火取消的换弹。备用子弹无限，动画结束时才把弹匣补满。
    /// </summary>
    public bool BeginReload(bool emptyMagazine, float animationDuration)
    {
        if (!CanReload)
            return false;

        IsReloading = true;
        StopLocalSustainedFire();
        AudioClip selectedReloadSound = emptyMagazine ? emptyReloadSound : partialReloadSound;
        if (selectedReloadSound != null && localOneShotSource != null)
            localOneShotSource.PlayOneShot(selectedReloadSound, 1f);
        onReloadStarted?.Invoke(emptyMagazine);

        float duration = animationDuration > 0f ? animationDuration : fallbackReloadDuration;
        reloadCoroutine = StartCoroutine(ReloadRoutine(Mathf.Max(0.05f, duration)));
        return true;
    }

    private IEnumerator ReloadRoutine(float duration)
    {
        yield return new WaitForSeconds(duration);
        CurrentAmmo = magazineSize;
        IsReloading = false;
        reloadCoroutine = null;
    }

    /// <summary>远端镜像使用：播放附着在第三人称角色位置上的 3D 枪声。</summary>
    public void PlayRemoteShotSound()
    {
        RegisterShotSound(
            remoteOneShotSource,
            remoteSustainedSource,
            ref remoteLastSoundShotTime,
            ref remoteSoundShotStreak,
            ref remoteSustainedWanted);
    }

    /// <summary>远端镜像使用：停止连发层并播放附着在角色位置上的 3D 换弹声。</summary>
    public void PlayRemoteReloadSound(bool emptyMagazine)
    {
        remoteSoundShotStreak = 0;
        remoteSustainedWanted = false;
        AudioClip selectedReloadSound = emptyMagazine ? emptyReloadSound : partialReloadSound;
        if (selectedReloadSound != null && remoteOneShotSource != null)
            remoteOneShotSource.PlayOneShot(selectedReloadSound, 1f);
    }

    private void PlayLocalShotSound()
    {
        RegisterShotSound(
            localOneShotSource,
            localSustainedSource,
            ref localLastSoundShotTime,
            ref localSoundShotStreak,
            ref localSustainedWanted);
    }

    private void RegisterShotSound(
        AudioSource oneShotSource,
        AudioSource sustainedSource,
        ref float lastShotSoundTime,
        ref int shotStreak,
        ref bool sustainedWanted)
    {
        float burstTimeout = Mathf.Max(0.08f, bulletInterval * 1.75f);
        shotStreak = Time.time - lastShotSoundTime <= burstTimeout ? shotStreak + 1 : 1;
        lastShotSoundTime = Time.time;

        if (singleShotSound != null && oneShotSource != null)
            oneShotSource.PlayOneShot(singleShotSound, singleShotVolume);

        sustainedWanted = sustainedFireSound != null && shotStreak >= sustainedFireStartShot;
        if (sustainedWanted && sustainedSource != null && !sustainedSource.isPlaying)
        {
            sustainedSource.clip = sustainedFireSound;
            sustainedSource.loop = true;
            sustainedSource.volume = 0f;
            sustainedSource.Play();
        }
    }

    private void StopLocalSustainedFire()
    {
        localSoundShotStreak = 0;
        localSustainedWanted = false;
    }

    private void UpdateSustainedSource(AudioSource source, bool wanted)
    {
        if (source == null)
            return;

        float targetVolume = wanted ? sustainedFireVolume : 0f;
        source.volume = Mathf.MoveTowards(
            source.volume,
            targetVolume,
            Mathf.Max(0.01f, soundFadeSpeed) * Time.deltaTime);
        if (!wanted && source.isPlaying && source.volume <= 0.001f)
            source.Stop();
    }

    private void CreateAudioSources()
    {
        localOneShotSource = CreateAudioSource(0f, false);
        localSustainedSource = CreateAudioSource(0f, true);
        remoteOneShotSource = CreateAudioSource(1f, false);
        remoteSustainedSource = CreateAudioSource(1f, true);
    }

    private AudioSource CreateAudioSource(float spatialBlend, bool loop)
    {
        AudioSource source = gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = loop;
        source.spatialBlend = spatialBlend;
        source.rolloffMode = AudioRolloffMode.Logarithmic;
        source.minDistance = remoteMinDistance;
        source.maxDistance = remoteMaxDistance;
        source.dopplerLevel = spatialBlend > 0f ? 0.25f : 0f;
        return source;
    }

    private void OnDisable()
    {
        if (reloadCoroutine != null)
        {
            StopCoroutine(reloadCoroutine);
            reloadCoroutine = null;
        }
        IsReloading = false;
        localSustainedWanted = false;
        remoteSustainedWanted = false;
        if (localSustainedSource != null)
            localSustainedSource.Stop();
        if (remoteSustainedSource != null)
            remoteSustainedSource.Stop();
    }

    public float CalculateSpread(bool moving, bool sprinting, bool grounded, bool aiming, int sprayIndex)
    {
        float spread = baseSpread;
        if (!grounded)
            spread += airSpreadPenalty;
        else if (sprinting)
            spread += sprintSpreadPenalty;
        else if (moving)
            spread += moveSpreadPenalty;

        spread += Mathf.Min(Mathf.Max(0, sprayIndex) * spreadPerShot, maxSpraySpread);
        if (aiming)
            spread *= aimingSpreadMultiplier;
        return Mathf.Max(0f, spread);
    }

    private Vector3 ApplySpread(Vector3 direction, float spreadDegrees)
    {
        if (spreadDegrees <= 0f)
            return direction;

        Vector2 randomPoint = UnityEngine.Random.insideUnitCircle * spreadDegrees;
        Quaternion spreadRotation = Quaternion.LookRotation(direction) *
                                    Quaternion.Euler(-randomPoint.y, randomPoint.x, 0f);
        return spreadRotation * Vector3.forward;
    }

    private Vector3 PerformHitscan(Vector3 origin, Vector3 direction)
    {
        if (ownerModel == null)
            ownerModel = GetComponentInParent<PlayerModel>();

        Vector3 traceEnd = origin + direction * range;

        int hitCount = Physics.RaycastNonAlloc(
            origin,
            direction,
            hitBuffer,
            range,
            hitMask,
            QueryTriggerInteraction.Ignore);

        RaycastHit nearestHit = default;
        float nearestDistance = range;
        bool hasHit = false;
        for (int i = 0; i < hitCount; i++)
        {
            RaycastHit hit = hitBuffer[i];
            if (hit.collider == null || IsOwnerCollider(hit.collider.transform))
                continue;
            if (hit.distance >= nearestDistance)
                continue;

            nearestDistance = hit.distance;
            nearestHit = hit;
            hasHit = true;
        }

        if (!hasHit)
            return traceEnd;

        traceEnd = nearestHit.point;

        DamageHitbox hitbox = nearestHit.collider.GetComponent<DamageHitbox>();
        EnemyBase enemy = hitbox != null
            ? hitbox.Enemy
            : nearestHit.collider.GetComponentInParent<EnemyBase>();
        if (enemy == null)
            return traceEnd;

        float multiplier = hitbox != null
            ? hitbox.damageMultiplier
            : CalculateFallbackDamageMultiplier(nearestHit.collider, nearestHit.point);
        enemy.Hurt(damage * multiplier, nearestHit.point, direction);
        return traceEnd;
    }

    private float CalculateFallbackDamageMultiplier(Collider hitCollider, Vector3 hitPoint)
    {
        if (hitCollider == null || hitCollider.bounds.size.y <= 0.0001f)
            return 1f;

        Bounds bounds = hitCollider.bounds;
        float normalizedHeight = Mathf.InverseLerp(bounds.min.y, bounds.max.y, hitPoint.y);
        if (normalizedHeight >= fallbackHeadHeight)
            return headDamageMultiplier;
        if (normalizedHeight <= fallbackLimbHeight)
            return limbDamageMultiplier;
        return 1f;
    }

    private bool IsOwnerCollider(Transform hitTransform)
    {
        if (ownerModel == null || hitTransform == null)
            return false;
        Transform root = ownerModel.transform;
        return hitTransform == root || hitTransform.IsChildOf(root);
    }

    /// <summary>
    /// 生成纯视觉曳光与枪口火花，不参与伤害判定。
    /// </summary>
    public void SpawnVisual(Vector3 spawnPos, Vector3 direction)
    {
        if (bulletEffectPrefab != null)
        {
            PlayerWeapnBullet bulletEffect = Instantiate(bulletEffectPrefab, spawnPos, Quaternion.identity);
            bulletEffect.transform.forward = direction; // 设置子弹朝向
        }
        // 实例化火花预制体
        if (bulletSparkPrefab != null)
        {
            GameObject spark = Instantiate(bulletSparkPrefab, spawnPos, Quaternion.identity);
            spark.transform.forward = direction; // 设置火花朝向
        }
    }
}
