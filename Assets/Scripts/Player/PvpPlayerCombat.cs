using System.Collections;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 仅挂在 PVP 网络角色上的服务器权威生命/死亡/复活组件。
/// </summary>
[DisallowMultipleComponent]
public class PvpPlayerCombat : NetworkBehaviour
{
    private const float ShotOriginProbeRadius = 0.02f;
    private static readonly Collider[] ShotOriginOverlapBuffer = new Collider[32];
    private const float PredictedImpactMatchDistance = 0.3f;
    private const float PredictedImpactLifetime = 0.75f;

    private struct PredictedImpact
    {
        public ulong TargetNetworkObjectId;
        public Vector3 HitPoint;
        public bool IsHeadshot;
        public float ExpiresAt;
    }

    // 仅存在于各客户端进程：射手先显示血花，服务器确认到达后在射手端消除重复特效。
    private static readonly List<PredictedImpact> PredictedImpacts =
        new List<PredictedImpact>(16);

    [Tooltip("PVP 最大生命值")]
    public float maxHealth = 100f;

    [Tooltip("死亡后复活等待秒数")]
    public float respawnDelay = 3f;

    [Header("PVP 头部命中区")]
    [Tooltip("跟随 Humanoid 头部骨骼的球形命中区半径")]
    public float headHitboxRadius = 0.14f;

    [Tooltip("头部命中区相对头部骨骼的世界垂直偏移")]
    public float headHitboxVerticalOffset = 0.06f;

    [Header("PVP 命中反馈")]
    [Tooltip("身体命中时在真实命中点生成的红色血花")]
    public GameObject bodyBloodImpactPrefab;

    [Tooltip("爆头时在真实命中点生成的较大红色血花")]
    public GameObject headBloodImpactPrefab;

    [Tooltip("血花自动销毁时间")]
    public float bloodImpactLifetime = 3f;

    [Tooltip("本地玩家被敌方爆头时播放的 2D 音效")]
    public AudioClip headshotReceivedSound;

    [Range(0f, 1f)]
    [Tooltip("被爆头音效音量")]
    public float headshotReceivedVolume = 1f;

    /// <summary>开局生成前由服务器从房间快照写入，之后随玩家对象同步，不再依赖客户端大厅列表时序。</summary>
    public readonly NetworkVariable<byte> TeamValue = new NetworkVariable<byte>(
        (byte)LobbyTeam.None,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public readonly NetworkVariable<byte> TeamNumberValue = new NetworkVariable<byte>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public readonly NetworkVariable<FixedString64Bytes> DisplayNameValue =
        new NetworkVariable<FixedString64Bytes>(
            default,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

    public readonly NetworkVariable<float> CurrentHealth = new NetworkVariable<float>(
        100f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public readonly NetworkVariable<bool> IsDead = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public readonly NetworkVariable<double> RespawnAtServerTime = new NetworkVariable<double>(
        0d,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public readonly NetworkVariable<int> Kills = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public readonly NetworkVariable<int> Deaths = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public readonly NetworkVariable<int> HeadshotKills = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private PlayerNetworkSync m_NetworkSync;
    private Renderer[] m_Renderers;
    private bool[] m_RendererEnabledStates;
    private Collider[] m_Colliders;
    private bool[] m_ColliderEnabledStates;
    private Coroutine m_RespawnCoroutine;
    private bool m_IsPvpSession;
    private AudioSource m_LocalHeadshotAudioSource;

    public LobbyTeam Team => (LobbyTeam)TeamValue.Value;
    public int TeamNumber => TeamNumberValue.Value;
    public string DisplayName => DisplayNameValue.Value.ToString();
    public float HeadshotRate => Kills.Value > 0 ? HeadshotKills.Value * 100f / Kills.Value : 0f;

    public float RespawnSecondsRemaining
    {
        get
        {
            if (!IsDead.Value || NetworkManager.Singleton == null)
                return 0f;
            return Mathf.Max(0f, (float)(RespawnAtServerTime.Value - NetworkManager.Singleton.ServerTime.Time));
        }
    }

    /// <summary>必须在 NetworkObject.Spawn 前调用，让队伍、编号和名字进入玩家对象的首次网络快照。</summary>
    public void ConfigureBeforeSpawn(LobbyTeam team, int teamNumber, string displayName)
    {
        if (NetworkObject != null && NetworkObject.IsSpawned)
            return;

        string safeName = string.IsNullOrWhiteSpace(displayName) ? "玩家" : displayName.Trim();
        if (safeName.Length > 24)
            safeName = safeName.Substring(0, 24);
        while (safeName.Length > 0 && Encoding.UTF8.GetByteCount(safeName) > 60)
            safeName = safeName.Substring(0, safeName.Length - 1);
        if (safeName.Length == 0)
            safeName = "玩家";
        TeamValue.Reset((byte)team);
        TeamNumberValue.Reset((byte)Mathf.Clamp(teamNumber, 1, LobbyState.PvpTeamCapacity));
        DisplayNameValue.Reset(new FixedString64Bytes(safeName));
        Kills.Reset(0);
        Deaths.Reset(0);
        HeadshotKills.Reset(0);
    }

    public override void OnNetworkSpawn()
    {
        LobbyState lobby = LobbyManager.Instance != null ? LobbyManager.Instance.CurrentLobby : null;
        m_IsPvpSession = Team == LobbyTeam.Red || Team == LobbyTeam.Blue;
        if (!m_IsPvpSession)
        {
            enabled = false;
            return;
        }

        m_NetworkSync = GetComponent<PlayerNetworkSync>();
        // 服务器使用它做权威爆头判定；客户端使用同样的区域做即时命中反馈预测。
        EnsureHeadHitbox();
        CaptureVisualAndColliderStates();

        IsDead.OnValueChanged += HandleDeadChanged;
        if (lobby != null)
            lobby.OnChanged += HandleMatchStateChanged;

        if (IsServer)
        {
            CurrentHealth.Value = maxHealth;
            IsDead.Value = false;
            RespawnAtServerTime.Value = 0d;
            Kills.Value = 0;
            Deaths.Value = 0;
            HeadshotKills.Value = 0;
        }

        ApplyDeadState(IsDead.Value);
        HandleMatchStateChanged();
    }

    public override void OnNetworkDespawn()
    {
        IsDead.OnValueChanged -= HandleDeadChanged;
        LobbyState lobby = LobbyManager.Instance != null ? LobbyManager.Instance.CurrentLobby : null;
        if (lobby != null)
            lobby.OnChanged -= HandleMatchStateChanged;

        if (m_RespawnCoroutine != null)
            StopCoroutine(m_RespawnCoroutine);
    }

    /// <summary>由 PlayerNetworkSync 的 ServerRpc 调用，在服务器上重新判定一发射线。</summary>
    public static void ProcessServerShot(
        PlayerNetworkSync shooter,
        Vector3 requestedOrigin,
        Vector3 requestedDirection,
        PlayerWeapon weapon)
    {
        if (shooter == null || weapon == null || !shooter.IsServer)
            return;

        LobbyManager lobbyManager = LobbyManager.Instance;
        LobbyState lobby = lobbyManager != null ? lobbyManager.CurrentLobby : null;
        if (lobby == null || lobby.GameMode != LobbyGameMode.PVP || lobby.IsPvpMatchOver)
            return;

        PvpPlayerCombat shooterCombat = shooter.GetComponent<PvpPlayerCombat>();
        if (shooterCombat == null || shooterCombat.IsDead.Value)
            return;

        LobbyTeam shooterTeam = shooterCombat.Team;
        if (shooterTeam != LobbyTeam.Red && shooterTeam != LobbyTeam.Blue)
            return;

        Vector3 safeOrigin = requestedOrigin;
        Vector3 expectedCenter = shooter.transform.position + Vector3.up * 1.2f;
        if ((safeOrigin - expectedCenter).sqrMagnitude > 36f)
            safeOrigin = expectedCenter;

        Vector3 direction = requestedDirection.sqrMagnitude > 0.000001f
            ? requestedDirection.normalized
            : shooter.transform.forward;

        Physics.SyncTransforms();
        Collider originSolidCollider = null;
        Vector3 originSolidPoint = safeOrigin;
        PvpHeadHitbox originHeadHitbox = null;
        FindShotOriginOverlaps(
            shooter.transform,
            safeOrigin,
            weapon.hitMask,
            out originSolidCollider,
            out originSolidPoint,
            out originHeadHitbox);

        RaycastHit[] hits = Physics.RaycastAll(
            safeOrigin,
            direction,
            weapon.range,
            weapon.hitMask,
            QueryTriggerInteraction.Collide);

        Collider nearestSolidCollider = originSolidCollider;
        float nearestSolidDistance = originSolidCollider != null ? 0f : weapon.range;
        Vector3 nearestSolidPoint = originSolidPoint;
        PvpHeadHitbox nearestHeadHitbox = originHeadHitbox;
        float nearestHeadDistance = originHeadHitbox != null ? 0f : weapon.range;
        Vector3 nearestHeadPoint = safeOrigin;
        foreach (RaycastHit hit in hits)
        {
            if (hit.collider == null)
                continue;
            Transform hitTransform = hit.collider.transform;
            if (hitTransform == shooter.transform || hitTransform.IsChildOf(shooter.transform))
                continue;

            PvpHeadHitbox headHitbox = hit.collider.GetComponent<PvpHeadHitbox>();
            if (hit.collider.isTrigger)
            {
                if (headHitbox != null && hit.distance < nearestHeadDistance)
                {
                    nearestHeadHitbox = headHitbox;
                    nearestHeadDistance = hit.distance;
                    nearestHeadPoint = hit.point;
                }
                continue;
            }

            if (hit.distance < nearestSolidDistance)
            {
                nearestSolidCollider = hit.collider;
                nearestSolidDistance = hit.distance;
                nearestSolidPoint = hit.point;
            }
        }

        PvpPlayerCombat victim = nearestSolidCollider != null
            ? nearestSolidCollider.GetComponentInParent<PvpPlayerCombat>()
            : null;
        bool isHeadshot = false;
        Vector3 hitPoint = nearestSolidCollider != null ? nearestSolidPoint : nearestHeadPoint;

        if (victim != null)
        {
            // 根 CharacterController 会包住头部球；只要同一受害者的专用头部球也被射线穿过，按爆头处理。
            Vector3 headHitPoint;
            isHeadshot = originHeadHitbox != null && originHeadHitbox.Owner == victim;
            if (!isHeadshot)
                isHeadshot = TryGetHeadHitForVictim(hits, shooter.transform, victim, out headHitPoint);
            else
                headHitPoint = safeOrigin;
            if (isHeadshot)
                hitPoint = headHitPoint;
        }
        else if (nearestHeadHitbox != null && nearestHeadDistance < nearestSolidDistance)
        {
            victim = nearestHeadHitbox.Owner;
            isHeadshot = true;
            hitPoint = nearestHeadPoint;
        }

        if (victim == null || victim == shooterCombat)
            return;

        float damageMultiplier = isHeadshot
            ? weapon.headDamageMultiplier
            : CalculateBodyHitMultiplier(nearestSolidCollider, nearestSolidPoint, weapon);
        victim.ApplyServerDamage(
            shooterCombat,
            weapon.damage * damageMultiplier,
            isHeadshot,
            hitPoint,
            direction);
    }

    /// <summary>
    /// Unity 射线不会返回起点已经位于内部的碰撞体。第一人称枪口贴近玩家时可能伸进
    /// CharacterController，因此先用一个极小范围探针把该碰撞体作为距离 0 的命中。
    /// </summary>
    private static void FindShotOriginOverlaps(
        Transform shooterTransform,
        Vector3 origin,
        LayerMask hitMask,
        out Collider nearestSolidCollider,
        out Vector3 nearestSolidPoint,
        out PvpHeadHitbox headHitbox)
    {
        nearestSolidCollider = null;
        nearestSolidPoint = origin;
        headHitbox = null;
        float nearestSolidSqrDistance = float.MaxValue;
        float nearestHeadSqrDistance = float.MaxValue;

        int overlapCount = Physics.OverlapSphereNonAlloc(
            origin,
            ShotOriginProbeRadius,
            ShotOriginOverlapBuffer,
            hitMask,
            QueryTriggerInteraction.Collide);

        for (int i = 0; i < overlapCount; i++)
        {
            Collider candidate = ShotOriginOverlapBuffer[i];
            if (candidate == null)
                continue;

            Transform candidateTransform = candidate.transform;
            if (candidateTransform == shooterTransform || candidateTransform.IsChildOf(shooterTransform))
                continue;

            Vector3 closestPoint = candidate.ClosestPoint(origin);
            float sqrDistance = (closestPoint - origin).sqrMagnitude;
            PvpHeadHitbox candidateHeadHitbox = candidate.GetComponent<PvpHeadHitbox>();
            if (candidate.isTrigger)
            {
                if (candidateHeadHitbox != null && sqrDistance < nearestHeadSqrDistance)
                {
                    headHitbox = candidateHeadHitbox;
                    nearestHeadSqrDistance = sqrDistance;
                }
                continue;
            }

            if (sqrDistance < nearestSolidSqrDistance)
            {
                nearestSolidCollider = candidate;
                nearestSolidPoint = closestPoint;
                nearestSolidSqrDistance = sqrDistance;
            }
        }
    }

    private static bool TryGetHeadHitForVictim(
        RaycastHit[] hits,
        Transform shooterTransform,
        PvpPlayerCombat victim,
        out Vector3 headHitPoint)
    {
        headHitPoint = default;
        float nearestDistance = float.MaxValue;
        bool found = false;
        foreach (RaycastHit hit in hits)
        {
            if (hit.collider == null)
                continue;
            Transform hitTransform = hit.collider.transform;
            if (hitTransform == shooterTransform || hitTransform.IsChildOf(shooterTransform))
                continue;

            PvpHeadHitbox headHitbox = hit.collider.GetComponent<PvpHeadHitbox>();
            if (headHitbox != null && headHitbox.Owner == victim && hit.distance < nearestDistance)
            {
                nearestDistance = hit.distance;
                headHitPoint = hit.point;
                found = true;
            }
        }
        return found;
    }

    private static float CalculateBodyHitMultiplier(Collider collider, Vector3 hitPoint, PlayerWeapon weapon)
    {
        if (collider == null || collider.bounds.size.y <= 0.0001f)
            return 1f;

        Bounds bounds = collider.bounds;
        float normalizedHeight = Mathf.InverseLerp(bounds.min.y, bounds.max.y, hitPoint.y);
        if (normalizedHeight <= weapon.fallbackLimbHeight)
            return weapon.limbDamageMultiplier;
        return 1f;
    }

    private void ApplyServerDamage(
        PvpPlayerCombat attacker,
        float damage,
        bool isHeadshot,
        Vector3 hitPoint,
        Vector3 shotDirection)
    {
        if (!IsServer || !m_IsPvpSession || IsDead.Value || damage <= 0f)
            return;

        LobbyState lobby = LobbyManager.Instance != null ? LobbyManager.Instance.CurrentLobby : null;
        if (lobby == null || lobby.IsPvpMatchOver)
            return;

        LobbyTeam attackerTeam = attacker != null ? attacker.Team : LobbyTeam.None;
        LobbyTeam victimTeam = Team;
        if (attackerTeam == LobbyTeam.None || victimTeam == LobbyTeam.None || attackerTeam == victimTeam)
            return; // 同队伤害和无效队伍直接拒绝

        CurrentHealth.Value = Mathf.Max(0f, CurrentHealth.Value - damage);
        ShowHitFeedbackClientRpc(hitPoint, shotDirection, isHeadshot);
        if (CurrentHealth.Value > 0f)
            return;

        IsDead.Value = true;
        RespawnAtServerTime.Value = NetworkManager.Singleton.ServerTime.Time + respawnDelay;
        Deaths.Value++;
        attacker.Kills.Value++;
        if (isHeadshot)
            attacker.HeadshotKills.Value++;
        lobby.RegisterPvpKill(attackerTeam);

        if (!lobby.IsPvpMatchOver)
            m_RespawnCoroutine = StartCoroutine(RespawnAfterDelay());
    }

    [ClientRpc]
    private void ShowHitFeedbackClientRpc(Vector3 hitPoint, Vector3 shotDirection, bool isHeadshot)
    {
        if (!ConsumePredictedImpact(hitPoint, isHeadshot))
            SpawnBloodImpact(hitPoint, shotDirection, isHeadshot);

        // 被爆头反馈只给受害玩家本人播放；血花仍由此 RPC 在所有客户端生成。
        if (isHeadshot && IsOwner && headshotReceivedSound != null)
        {
            if (m_LocalHeadshotAudioSource == null)
            {
                m_LocalHeadshotAudioSource = gameObject.AddComponent<AudioSource>();
                m_LocalHeadshotAudioSource.playOnAwake = false;
                m_LocalHeadshotAudioSource.loop = false;
                m_LocalHeadshotAudioSource.spatialBlend = 0f;
            }
            m_LocalHeadshotAudioSource.PlayOneShot(headshotReceivedSound, headshotReceivedVolume);
        }
    }

    /// <summary>
    /// 射手客户端即时显示预测血花；不修改任何 NetworkVariable，也不会触发死亡或计分。
    /// </summary>
    public void ShowPredictedHitFeedback(Vector3 hitPoint, Vector3 shotDirection, bool isHeadshot)
    {
        if (!IsSpawned || !m_IsPvpSession || IsDead.Value)
            return;

        CleanupExpiredPredictedImpacts();
        SpawnBloodImpact(hitPoint, shotDirection, isHeadshot);
        PredictedImpacts.Add(new PredictedImpact
        {
            TargetNetworkObjectId = NetworkObjectId,
            HitPoint = hitPoint,
            IsHeadshot = isHeadshot,
            ExpiresAt = Time.unscaledTime + PredictedImpactLifetime,
        });
    }

    private void SpawnBloodImpact(Vector3 hitPoint, Vector3 shotDirection, bool isHeadshot)
    {
        GameObject bloodPrefab = isHeadshot ? headBloodImpactPrefab : bodyBloodImpactPrefab;
        if (bloodPrefab != null)
        {
            Quaternion rotation = shotDirection.sqrMagnitude > 0.000001f
                ? Quaternion.LookRotation(-shotDirection.normalized)
                : Quaternion.identity;
            GameObject bloodImpact = Instantiate(bloodPrefab, hitPoint, rotation);
            Destroy(bloodImpact, Mathf.Max(0.1f, bloodImpactLifetime));
        }
    }

    private bool ConsumePredictedImpact(Vector3 hitPoint, bool isHeadshot)
    {
        float now = Time.unscaledTime;
        float maxSqrDistance = PredictedImpactMatchDistance * PredictedImpactMatchDistance;
        for (int i = PredictedImpacts.Count - 1; i >= 0; i--)
        {
            PredictedImpact predicted = PredictedImpacts[i];
            if (predicted.ExpiresAt < now)
            {
                PredictedImpacts.RemoveAt(i);
                continue;
            }

            if (predicted.TargetNetworkObjectId != NetworkObjectId ||
                predicted.IsHeadshot != isHeadshot ||
                (predicted.HitPoint - hitPoint).sqrMagnitude > maxSqrDistance)
            {
                continue;
            }

            PredictedImpacts.RemoveAt(i);
            return true;
        }

        return false;
    }

    private static void CleanupExpiredPredictedImpacts()
    {
        float now = Time.unscaledTime;
        for (int i = PredictedImpacts.Count - 1; i >= 0; i--)
        {
            if (PredictedImpacts[i].ExpiresAt < now)
                PredictedImpacts.RemoveAt(i);
        }
    }

    private void EnsureHeadHitbox()
    {
        Animator animator = m_NetworkSync != null && m_NetworkSync.playerModel != null
            ? m_NetworkSync.playerModel.animator
            : GetComponentInChildren<Animator>(true);
        if (animator == null || animator.avatar == null || !animator.avatar.isValid || !animator.isHuman)
        {
            Debug.LogError("[PVP] 无法创建头部命中区：角色 Animator 不是有效的 Humanoid，角色=" + name);
            return;
        }

        Transform headBone = animator.GetBoneTransform(HumanBodyBones.Head);
        if (headBone == null)
        {
            Debug.LogError("[PVP] 无法创建头部命中区：未找到 Humanoid Head 骨骼，角色=" + name);
            return;
        }

        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        PvpHeadHitbox hitbox = GetComponentInChildren<PvpHeadHitbox>(true);
        if (hitbox == null)
        {
            GameObject hitboxObject = new GameObject("__PvpHeadHitbox");
            hitboxObject.layer = gameObject.layer;
            hitbox = hitboxObject.AddComponent<PvpHeadHitbox>();
        }
        hitbox.Initialize(this, headBone, headHitboxRadius, headHitboxVerticalOffset);
    }

    private IEnumerator RespawnAfterDelay()
    {
        yield return new WaitForSeconds(respawnDelay);

        LobbyManager lobbyManager = LobbyManager.Instance;
        LobbyState lobby = lobbyManager != null ? lobbyManager.CurrentLobby : null;
        if (!IsServer || lobby == null || lobby.IsPvpMatchOver)
            yield break;

        if (!lobbyManager.TryGetPvpSpawnForClient(OwnerClientId, Team, out Vector3 position, out Quaternion rotation))
            yield break;

        PlaceAtSpawnServer(position, rotation);
        CurrentHealth.Value = maxHealth;
        RespawnAtServerTime.Value = 0d;
        IsDead.Value = false;
        m_RespawnCoroutine = null;
    }

    /// <summary>服务器在首次生成和复活时强制把所有端校准到同一基地位置。</summary>
    public void PlaceAtSpawnServer(Vector3 position, Quaternion rotation)
    {
        if (!IsServer)
            return;

        ApplySpawnTransform(position, rotation);
        OwnerNetworkTransform networkTransform = GetComponent<OwnerNetworkTransform>();
        if (networkTransform != null)
            networkTransform.SetState(position, rotation, transform.localScale, false);
        ForceSpawnTransformClientRpc(position, rotation);
    }

    [ClientRpc]
    private void ForceSpawnTransformClientRpc(Vector3 position, Quaternion rotation)
    {
        ApplySpawnTransform(position, rotation);
    }

    private void ApplySpawnTransform(Vector3 position, Quaternion rotation)
    {
        CharacterController characterController = GetComponent<CharacterController>();
        bool wasEnabled = characterController != null && characterController.enabled;
        if (wasEnabled)
            characterController.enabled = false;
        transform.SetPositionAndRotation(position, rotation);
        if (wasEnabled)
            characterController.enabled = true;
    }

    private void HandleDeadChanged(bool previousValue, bool newValue)
    {
        ApplyDeadState(newValue);
    }

    private void HandleMatchStateChanged()
    {
        LobbyState lobby = LobbyManager.Instance != null ? LobbyManager.Instance.CurrentLobby : null;
        bool matchOver = lobby != null && lobby.IsPvpMatchOver;
        SetOwnerControlEnabled(!IsDead.Value && !matchOver);
    }

    private void ApplyDeadState(bool dead)
    {
        if (m_Renderers != null)
        {
            for (int i = 0; i < m_Renderers.Length; i++)
            {
                if (m_Renderers[i] != null)
                    m_Renderers[i].enabled = !dead && m_RendererEnabledStates[i];
            }
        }

        if (m_Colliders != null)
        {
            for (int i = 0; i < m_Colliders.Length; i++)
            {
                if (m_Colliders[i] != null)
                    m_Colliders[i].enabled = !dead && m_ColliderEnabledStates[i];
            }
        }

        if (dead && m_NetworkSync != null && m_NetworkSync.playerModel != null)
            m_NetworkSync.playerModel.externalMoveVelocity = Vector3.zero;
        HandleMatchStateChanged();
    }

    private void SetOwnerControlEnabled(bool controlEnabled)
    {
        if (!IsOwner)
            return;

        PlayerController controller = PlayerController.instance;
        if (controller != null && m_NetworkSync != null &&
            controller.currentPlayerModel == m_NetworkSync.playerModel)
            controller.enabled = controlEnabled;
    }

    private void CaptureVisualAndColliderStates()
    {
        m_Renderers = GetComponentsInChildren<Renderer>(true);
        m_RendererEnabledStates = new bool[m_Renderers.Length];
        for (int i = 0; i < m_Renderers.Length; i++)
            m_RendererEnabledStates[i] = m_Renderers[i] != null && m_Renderers[i].enabled;

        m_Colliders = GetComponentsInChildren<Collider>(true);
        m_ColliderEnabledStates = new bool[m_Colliders.Length];
        for (int i = 0; i < m_Colliders.Length; i++)
            m_ColliderEnabledStates[i] = m_Colliders[i] != null && m_Colliders[i].enabled;
    }
}
