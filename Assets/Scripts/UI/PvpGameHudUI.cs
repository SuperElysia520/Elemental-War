using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>绑定用户在 GamePVP 场景中制作的 GamePanel，不创建任何 HUD 图形或文本。</summary>
[DisallowMultipleComponent]
public class PvpGameHudUI : MonoBehaviour
{
    private sealed class TeammateRow
    {
        public GameObject Root;
        public Text Number;
        public Text PlayerName;
        public Image HealthBar;

        public bool IsValid => Root != null && Number != null && PlayerName != null && HealthBar != null;
    }

    private readonly List<TeammateRow> m_TeammateRows =
        new List<TeammateRow>(LobbyState.PvpTeamCapacity);
    private readonly List<PvpPlayerCombat> m_TeamPlayers =
        new List<PvpPlayerCombat>(LobbyState.PvpTeamCapacity);

    private Image m_BlueProgressBar;
    private Image m_RedProgressBar;
    private Text m_BlueKillCount;
    private Text m_RedKillCount;
    private Text m_TotalKillCount;
    private Image m_SelfHealthBar;
    private Text m_SelfHealthNumber;
    private Text m_RemainingBullets;
    private PvpPlayerCombat m_LocalPlayer;
    private int m_LastDisplayedBullets = -1;
    private float m_NextRosterRefreshTime;

    public static void AttachTo(GameObject gamePanel)
    {
        if (gamePanel == null)
        {
            Debug.LogError("[PVP HUD] GamePVP 场景中没有找到 GamePanel");
            return;
        }

        if (gamePanel.GetComponent<PvpGameHudUI>() == null)
            gamePanel.AddComponent<PvpGameHudUI>();
    }

    private void Awake()
    {
        m_BlueProgressBar = GetImage("ScoringPanel/BlueProgressBar");
        m_BlueKillCount = GetText("ScoringPanel/BlueProgressBar/KillCount");
        m_TotalKillCount = GetText("ScoringPanel/TotalKillCount");
        if (m_TotalKillCount == null)
            m_TotalKillCount = GetText("ScoringPanel/TotaKillCount");
        m_RedProgressBar = GetImage("ScoringPanel/RedProgressBar");
        m_RedKillCount = GetText("ScoringPanel/RedProgressBar/KillCount");
        m_SelfHealthBar = GetImage("SelfHealthBarPanel/Health");
        m_SelfHealthNumber = GetText("SelfHealthBarPanel/HealthNumber");
        // WeaponPanel 是用户自行制作的可选面板；这里只按节点名读取，不创建或改动布局。
        m_RemainingBullets = GetText("WeaponPanel/RemainingBullets");
        BindTeammateRows();

        if (m_BlueProgressBar == null || m_BlueKillCount == null || m_TotalKillCount == null ||
            m_RedProgressBar == null || m_RedKillCount == null || m_SelfHealthBar == null ||
            m_SelfHealthNumber == null || m_TeammateRows.Count != LobbyState.PvpTeamCapacity)
        {
            Debug.LogError(
                "[PVP HUD] GamePanel 层级不完整，请保持 ScoringPanel、SelfHealthBarPanel、" +
                "TeamLifePanel 以及截图中的子节点命名");
            enabled = false;
            return;
        }

        m_TotalKillCount.text = LobbyState.PvpKillTarget.ToString();
        SetScore(0, 0);
        SetSelfHealth(null);
        HideAllTeammateRows();
    }

    private void Update()
    {
        UpdateScore();

        if (m_LocalPlayer == null || Time.unscaledTime >= m_NextRosterRefreshTime)
        {
            RefreshTeamRoster();
            m_NextRosterRefreshTime = Time.unscaledTime + 0.25f;
        }

        SetSelfHealth(m_LocalPlayer);
        UpdateTeammateRows();
        UpdateRemainingBullets();
    }

    private Image GetImage(string path)
    {
        Transform child = transform.Find(path);
        return child != null ? child.GetComponent<Image>() : null;
    }

    private Text GetText(string path)
    {
        Transform child = transform.Find(path);
        return child != null ? child.GetComponent<Text>() : null;
    }

    private void BindTeammateRows()
    {
        Transform teamLifePanel = transform.Find("TeamLifePanel");
        if (teamLifePanel == null)
            return;

        int rowCount = Mathf.Min(teamLifePanel.childCount, LobbyState.PvpTeamCapacity);
        for (int i = 0; i < rowCount; i++)
        {
            Transform rowTransform = teamLifePanel.GetChild(i);
            var row = new TeammateRow
            {
                Root = rowTransform.gameObject,
                Number = GetTextFromRow(rowTransform, "Serial Number/number"),
                HealthBar = GetImageFromRow(rowTransform, "HealthBar"),
                PlayerName = GetTextFromRow(rowTransform, "Name"),
            };
            if (row.IsValid)
                m_TeammateRows.Add(row);
        }
    }

    private static Text GetTextFromRow(Transform row, string path)
    {
        Transform child = row.Find(path);
        return child != null ? child.GetComponent<Text>() : null;
    }

    private static Image GetImageFromRow(Transform row, string path)
    {
        Transform child = row.Find(path);
        return child != null ? child.GetComponent<Image>() : null;
    }

    private void UpdateScore()
    {
        LobbyState lobby = LobbyManager.Instance != null ? LobbyManager.Instance.CurrentLobby : null;
        if (lobby == null || lobby.GameMode != LobbyGameMode.PVP)
        {
            SetScore(0, 0);
            return;
        }

        SetScore(lobby.BlueKills.Value, lobby.RedKills.Value);
    }

    private void SetScore(int blueKills, int redKills)
    {
        int target = LobbyState.PvpKillTarget;
        blueKills = Mathf.Clamp(blueKills, 0, target);
        redKills = Mathf.Clamp(redKills, 0, target);
        m_BlueKillCount.text = blueKills.ToString();
        m_RedKillCount.text = redKills.ToString();
        m_BlueProgressBar.fillAmount = target > 0 ? (float)blueKills / target : 0f;
        m_RedProgressBar.fillAmount = target > 0 ? (float)redKills / target : 0f;
    }

    private void RefreshTeamRoster()
    {
        PvpPlayerCombat[] allPlayers = FindObjectsOfType<PvpPlayerCombat>();
        m_LocalPlayer = null;
        for (int i = 0; i < allPlayers.Length; i++)
        {
            PvpPlayerCombat player = allPlayers[i];
            if (player != null && player.IsSpawned && player.IsOwner)
            {
                m_LocalPlayer = player;
                break;
            }
        }

        m_TeamPlayers.Clear();
        if (m_LocalPlayer == null)
            return;

        for (int i = 0; i < allPlayers.Length; i++)
        {
            PvpPlayerCombat player = allPlayers[i];
            if (player != null && player.IsSpawned && player.Team == m_LocalPlayer.Team)
                m_TeamPlayers.Add(player);
        }

        m_TeamPlayers.Sort((left, right) => left.TeamNumber.CompareTo(right.TeamNumber));
    }

    private void SetSelfHealth(PvpPlayerCombat player)
    {
        float health = player != null && !player.IsDead.Value ? player.CurrentHealth.Value : 0f;
        float maxHealth = player != null ? Mathf.Max(1f, player.maxHealth) : 1f;
        m_SelfHealthBar.fillAmount = Mathf.Clamp01(health / maxHealth);
        m_SelfHealthNumber.text = Mathf.CeilToInt(Mathf.Max(0f, health)).ToString();
    }

    private void UpdateTeammateRows()
    {
        for (int i = 0; i < m_TeammateRows.Count; i++)
        {
            TeammateRow row = m_TeammateRows[i];
            bool hasPlayer = i < m_TeamPlayers.Count && m_TeamPlayers[i] != null;
            row.Root.SetActive(hasPlayer);
            if (!hasPlayer)
                continue;

            PvpPlayerCombat player = m_TeamPlayers[i];
            float health = player.IsDead.Value ? 0f : player.CurrentHealth.Value;
            float maxHealth = Mathf.Max(1f, player.maxHealth);
            row.Number.text = Mathf.Clamp(player.TeamNumber, 1, LobbyState.PvpTeamCapacity).ToString();
            row.PlayerName.text = string.IsNullOrWhiteSpace(player.DisplayName) ? "玩家" : player.DisplayName;
            row.HealthBar.fillAmount = Mathf.Clamp01(health / maxHealth);
        }
    }

    private void HideAllTeammateRows()
    {
        for (int i = 0; i < m_TeammateRows.Count; i++)
            m_TeammateRows[i].Root.SetActive(false);
    }

    private void UpdateRemainingBullets()
    {
        if (m_RemainingBullets == null || m_LocalPlayer == null)
            return;

        PlayerNetworkSync networkSync = m_LocalPlayer.GetComponent<PlayerNetworkSync>();
        PlayerWeapon weapon = networkSync != null && networkSync.playerModel != null
            ? networkSync.playerModel.weapon
            : m_LocalPlayer.GetComponent<PlayerModel>()?.weapon;
        if (weapon == null || weapon.CurrentAmmo == m_LastDisplayedBullets)
            return;

        m_LastDisplayedBullets = weapon.CurrentAmmo;
        m_RemainingBullets.text = m_LastDisplayedBullets + " / 无限";
    }
}
