using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 联机玩家同步壳（NetworkBehaviour）：只做「状态上发」与「远端镜像」，不重写玩法逻辑。
/// - 本地玩家（Owner）：沿用 PlayerController 单例 + PlayerModel 状态机驱动，本组件只把动画参数上发。
/// - 远端玩家（非 Owner）：关闭本地状态机/AI 跟随，改由网络状态镜像动画，位置由 OwnerNetworkTransform 同步。
/// 位置/旋转同步由同物体上的 OwnerNetworkTransform（owner 权威）承担，本组件不处理。
/// </summary>
public class PlayerNetworkSync : NetworkBehaviour
{
    [Tooltip("同物体上的 PlayerModel（自动获取）")]
    public PlayerModel playerModel;

    private Animator animator;
    private int moveBlendHash;
    private int aimingXHash;
    private int aimingYHash;

    // 上次镜像到的远端状态：只在状态变化时 CrossFade 一次，否则每帧重置动画时间会冻结动作（滑行）
    private PlayerState lastRemoteState = PlayerState.Idle;
    private double lastAcceptedServerShotTime = -999d;
    private bool m_PvpNameplateResolved;

    // 动画参数（按 PlayerState 枚举的 int + 三个 blend 浮点）
    // 用 Owner 写权限：本地玩家把自己的动画状态上发，服务器与远端只读镜像。
    private readonly NetworkVariable<int> state = new NetworkVariable<int>(writePerm: NetworkVariableWritePermission.Owner);
    private readonly NetworkVariable<float> moveBlend = new NetworkVariable<float>(writePerm: NetworkVariableWritePermission.Owner);
    private readonly NetworkVariable<float> aimingX = new NetworkVariable<float>(writePerm: NetworkVariableWritePermission.Owner);
    private readonly NetworkVariable<float> aimingY = new NetworkVariable<float>(writePerm: NetworkVariableWritePermission.Owner);

    public override void OnNetworkSpawn()
    {
        if (playerModel == null)
            playerModel = GetComponent<PlayerModel>();

        animator = playerModel != null && playerModel.animator != null
            ? playerModel.animator
            : GetComponent<Animator>();

        moveBlendHash = Animator.StringToHash("MoveBlend");
        aimingXHash = Animator.StringToHash("AimingX");
        aimingYHash = Animator.StringToHash("AimingY");

        if (IsOwner)
        {
            EnsureLocalControl();
            if (playerModel != null && playerModel.weapon != null)
            {
                playerModel.weapon.onFire += OnLocalFire;
                playerModel.weapon.onReloadStarted += OnLocalReload;
            }
        }
        else
            DisableLocalLogic();

        SetupPvpTeammateNameplate();
    }

    public override void OnNetworkDespawn()
    {
        if (playerModel != null && playerModel.weapon != null)
        {
            playerModel.weapon.onFire -= OnLocalFire;
            playerModel.weapon.onReloadStarted -= OnLocalReload;
        }
    }

    void Update()
    {
        if (!m_PvpNameplateResolved)
            SetupPvpTeammateNameplate();

        if (animator == null || !IsSpawned)
            return;

        if (IsOwner)
            PublishLocalState();
        else
            ApplyRemoteState();
    }

    // ---- 本地玩家 ----

    private void EnsureLocalControl()
    {
        var pc = PlayerController.instance;
        if (pc == null || playerModel == null)
            return;

        // 让单机 PlayerController 指向这个联网角色；禁用 1/2/3 切换（联机一人一角）
        pc.disableModelSwitching = true;
        pc.currentPlayerModel = playerModel;
        playerModel.Enter();
        pc.ResetCameraTarget();

        // 联网角色从预制体实例化，瞄准约束的 source object 是空的，补绑到场景 AimTarget
        playerModel.BindAimTargetToConstraints(pc.AimTarget);
    }

    private void PublishLocalState()
    {
        state.Value = playerModel != null ? (int)playerModel.CurrentState : state.Value;
        moveBlend.Value = animator.GetFloat(moveBlendHash);
        aimingX.Value = animator.GetFloat(aimingXHash);
        aimingY.Value = animator.GetFloat(aimingYHash);
    }

    // ---- 远端玩家 ----

    private void DisableLocalLogic()
    {
        if (playerModel != null)
        {
            playerModel.Stop();      // 退出状态机，避免进入 AI 跟随分支
            playerModel.enabled = false;
        }

        // 远端位置由 OwnerNetworkTransform 直接写 transform，禁用会与之冲突的本地驱动组件。
        // PVP 必须在每个玩家客户端都保留远端 CharacterController，拥有者的 cc.Move 才能撞到其他玩家；
        // 其 PlayerModel 已关闭，不会在远端客户端重复驱动这个 CharacterController。
        var cc = GetComponent<CharacterController>();
        bool isPvpPlayer = GetComponent<PvpPlayerCombat>() != null;
        if (cc != null && !isPvpPlayer)
            cc.enabled = false;
        var agent = GetComponent<NavMeshAgent>();
        if (agent != null) agent.enabled = false;

        if (animator != null)
            animator.applyRootMotion = false;
    }

    private void ApplyRemoteState()
    {
        // 只在状态变化时 CrossFade，避免每帧重置动画时间导致动作冻结
        var current = (PlayerState)state.Value;
        if (current != lastRemoteState)
        {
            lastRemoteState = current;
            switch (current)
            {
                case PlayerState.Move:
                    animator.CrossFadeInFixedTime("Move", 0.1f, 0);
                    break;
                case PlayerState.Hover:
                    animator.CrossFadeInFixedTime("Hover", 0.1f, 0);
                    break;
                case PlayerState.Aiming:
                    animator.CrossFadeInFixedTime("Aiming", 0.1f, 0);
                    break;
                default:
                    animator.CrossFadeInFixedTime("Idle", 0.1f, 0);
                    break;
            }
        }

        animator.SetFloat(moveBlendHash, moveBlend.Value);
        animator.SetFloat(aimingXHash, aimingX.Value);
        animator.SetFloat(aimingYHash, aimingY.Value);
    }

    // ---- 开火同步 ----

    private void OnLocalFire(Vector3 spawnPos, Vector3 direction)
    {
        // 视觉枪口和真实射线起点分开发送：远端从枪口播曳光，服务器从相机射线复核 PVP 命中。
        Vector3 shotOrigin = playerModel != null && playerModel.weapon != null
            ? playerModel.weapon.LastShotOrigin
            : spawnPos;
        FireServerRpc(spawnPos, shotOrigin, direction);
    }

    [ServerRpc]
    private void FireServerRpc(
        Vector3 visualSpawnPos,
        Vector3 shotOrigin,
        Vector3 direction,
        ServerRpcParams rpcParams = default)
    {
        LobbyState lobby = LobbyManager.Instance != null ? LobbyManager.Instance.CurrentLobby : null;
        if (lobby != null && lobby.GameMode == LobbyGameMode.PVP)
        {
            if (lobby.IsPvpMatchOver || playerModel == null || playerModel.weapon == null)
                return;

            double now = NetworkManager.Singleton.ServerTime.Time;
            if (now - lastAcceptedServerShotTime < playerModel.weapon.bulletInterval * 0.8f)
                return;
            lastAcceptedServerShotTime = now;
            PvpPlayerCombat.ProcessServerShot(this, shotOrigin, direction, playerModel.weapon);
        }

        FireClientRpc(visualSpawnPos, direction);
    }

    [ClientRpc]
    private void FireClientRpc(Vector3 spawnPos, Vector3 direction, ClientRpcParams rpcParams = default)
    {
        // owner 已经自己生成过真实子弹，这里只为远端生成纯视觉镜像
        if (IsOwner || playerModel == null || playerModel.weapon == null)
            return;

        PlayerWeapon weapon = playerModel.weapon;
        // 射手上报的枪口世界坐标到达观察者时已经滞后于 NetworkTransform 插值。
        // 使用观察者当前渲染出来的第三人称枪口，确保曳光和枪口火花始终贴在武器上。
        Vector3 currentMuzzlePosition = weapon.bulletSpawnPoint != null
            ? weapon.bulletSpawnPoint.position
            : spawnPos;
        weapon.PlayRemoteShotSound();
        weapon.SpawnVisual(currentMuzzlePosition, direction);
    }

    // ---- 换弹音效同步 ----

    private void OnLocalReload(bool emptyMagazine)
    {
        ReloadServerRpc(emptyMagazine);
    }

    [ServerRpc]
    private void ReloadServerRpc(bool emptyMagazine)
    {
        ReloadClientRpc(emptyMagazine);
    }

    [ClientRpc]
    private void ReloadClientRpc(bool emptyMagazine)
    {
        // owner 已在本地播放 2D 换弹声；其他客户端播放角色位置上的 3D 换弹声。
        if (IsOwner || playerModel == null || playerModel.weapon == null)
            return;

        playerModel.weapon.PlayRemoteReloadSound(emptyMagazine);
    }

    private void SetupPvpTeammateNameplate()
    {
        if (IsOwner)
        {
            m_PvpNameplateResolved = true;
            return;
        }
        if (NetworkManager.Singleton == null)
            return;

        PvpPlayerCombat remoteCombat = GetComponent<PvpPlayerCombat>();
        if (remoteCombat == null)
        {
            m_PvpNameplateResolved = true;
            return;
        }

        NetworkObject localPlayer = NetworkManager.Singleton.LocalClient != null
            ? NetworkManager.Singleton.LocalClient.PlayerObject
            : null;
        PvpPlayerCombat localCombat = localPlayer != null
            ? localPlayer.GetComponent<PvpPlayerCombat>()
            : null;
        if (localCombat == null)
            return;

        LobbyTeam localTeam = localCombat.Team;
        LobbyTeam remoteTeam = remoteCombat.Team;
        if (localTeam == LobbyTeam.None || remoteTeam == LobbyTeam.None ||
            remoteCombat.TeamNumber < 1 || string.IsNullOrWhiteSpace(remoteCombat.DisplayName))
            return;

        m_PvpNameplateResolved = true;
        if (localTeam != remoteTeam)
            return;

        PvpTeammateNameplate nameplate = GetComponent<PvpTeammateNameplate>();
        if (nameplate == null)
            nameplate = gameObject.AddComponent<PvpTeammateNameplate>();
        nameplate.Initialize(remoteCombat.TeamNumber, remoteCombat.DisplayName);
    }
}
