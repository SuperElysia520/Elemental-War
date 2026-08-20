using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 绑定 GamePVP 场景中用户制作的 RecordPanel；不创建任何计分板 UI。
/// 按住 Tab 显示，松开隐藏。
/// </summary>
[DisallowMultipleComponent]
public class PvpScoreboardUI : MonoBehaviour
{
    private sealed class PlayerRow
    {
        public GameObject Root;
        public Text PlayerName;
        public Text Kills;
        public Text Deaths;
        public Text HeadshotRate;

        public bool IsValid => Root != null && PlayerName != null && Kills != null &&
                               Deaths != null && HeadshotRate != null;
    }

    private sealed class TeamSection
    {
        public readonly List<PlayerRow> Rows = new List<PlayerRow>(LobbyState.PvpTeamCapacity);
        public bool IsValid => Rows.Count == LobbyState.PvpTeamCapacity;
    }

    private readonly List<PvpPlayerCombat> m_OwnTeamPlayers =
        new List<PvpPlayerCombat>(LobbyState.PvpTeamCapacity);
    private readonly List<PvpPlayerCombat> m_EnemyTeamPlayers =
        new List<PvpPlayerCombat>(LobbyState.PvpTeamCapacity);

    private CanvasGroup m_CanvasGroup;
    private TeamSection m_OwnTeamSection;
    private TeamSection m_EnemyTeamSection;
    private bool m_IsVisible;

    public static void AttachTo(GameObject recordPanel)
    {
        if (recordPanel == null)
        {
            Debug.LogError("[PVP 计分板] GamePVP 场景中没有找到 RecordPanel");
            return;
        }

        if (recordPanel.GetComponent<PvpScoreboardUI>() == null)
            recordPanel.AddComponent<PvpScoreboardUI>();
    }

    private void Awake()
    {
        m_CanvasGroup = GetComponent<CanvasGroup>();
        if (m_CanvasGroup == null)
            m_CanvasGroup = gameObject.AddComponent<CanvasGroup>();

        m_OwnTeamSection = BindTeamSection("BlueTeam");
        m_EnemyTeamSection = BindTeamSection("RedTeam");
        if (!m_OwnTeamSection.IsValid || !m_EnemyTeamSection.IsValid)
        {
            Debug.LogError(
                "[PVP 计分板] RecordPanel 层级不完整。需要 MyTeam/MyTeam (1) -> List -> " +
                "5 个 Member，每个 Member 下包含 name、kill、die、headshot 普通 Text");
            enabled = false;
        }

        SetVisible(false);
        HideAllRows(m_OwnTeamSection);
        HideAllRows(m_EnemyTeamSection);
    }

    private void Update()
    {
        bool shouldShow = Input.GetKey(KeyCode.Tab);
        if (shouldShow != m_IsVisible)
            SetVisible(shouldShow);

        if (m_IsVisible)
            RefreshRows();
    }

    private TeamSection BindTeamSection(string sectionName)
    {
        var section = new TeamSection();
        Transform list = transform.Find(sectionName + "/List");
        if (list == null)
            return section;

        int rowCount = Mathf.Min(list.childCount, LobbyState.PvpTeamCapacity);
        for (int i = 0; i < rowCount; i++)
        {
            Transform rowTransform = list.GetChild(i);
            var row = new PlayerRow
            {
                Root = rowTransform.gameObject,
                PlayerName = GetRowText(rowTransform, "name"),
                Kills = GetRowText(rowTransform, "kill"),
                Deaths = GetRowText(rowTransform, "die"),
                HeadshotRate = GetRowText(rowTransform, "headshot"),
            };
            if (row.IsValid)
                section.Rows.Add(row);
        }
        return section;
    }

    private static Text GetRowText(Transform row, string childName)
    {
        Transform child = row.Find(childName);
        return child != null ? child.GetComponent<Text>() : null;
    }

    private void SetVisible(bool visible)
    {
        m_IsVisible = visible;
        m_CanvasGroup.alpha = visible ? 1f : 0f;
        m_CanvasGroup.interactable = false;
        m_CanvasGroup.blocksRaycasts = false;

        if (visible)
            RefreshRows();
    }

    private void RefreshRows()
    {
        PvpPlayerCombat[] players = FindObjectsOfType<PvpPlayerCombat>();
        PvpPlayerCombat localPlayer = null;
        for (int i = 0; i < players.Length; i++)
        {
            if (players[i] != null && players[i].IsSpawned && players[i].IsOwner)
            {
                localPlayer = players[i];
                break;
            }
        }

        if (localPlayer == null ||
            (localPlayer.Team != LobbyTeam.Red && localPlayer.Team != LobbyTeam.Blue))
        {
            HideAllRows(m_OwnTeamSection);
            HideAllRows(m_EnemyTeamSection);
            return;
        }

        m_OwnTeamPlayers.Clear();
        m_EnemyTeamPlayers.Clear();
        for (int i = 0; i < players.Length; i++)
        {
            PvpPlayerCombat player = players[i];
            if (player == null || !player.IsSpawned)
                continue;

            if (player.Team == localPlayer.Team)
                m_OwnTeamPlayers.Add(player);
            else if (player.Team == LobbyTeam.Red || player.Team == LobbyTeam.Blue)
                m_EnemyTeamPlayers.Add(player);
        }

        m_OwnTeamPlayers.Sort(ComparePlayers);
        m_EnemyTeamPlayers.Sort(ComparePlayers);
        FillSection(m_OwnTeamSection, m_OwnTeamPlayers);
        FillSection(m_EnemyTeamSection, m_EnemyTeamPlayers);
    }

    private static int ComparePlayers(PvpPlayerCombat left, PvpPlayerCombat right)
    {
        int killComparison = right.Kills.Value.CompareTo(left.Kills.Value);
        return killComparison != 0
            ? killComparison
            : left.TeamNumber.CompareTo(right.TeamNumber);
    }

    private static void FillSection(TeamSection section, List<PvpPlayerCombat> players)
    {
        for (int i = 0; i < section.Rows.Count; i++)
        {
            PlayerRow row = section.Rows[i];
            bool hasPlayer = i < players.Count;
            row.Root.SetActive(hasPlayer);
            if (!hasPlayer)
                continue;

            PvpPlayerCombat player = players[i];
            row.PlayerName.text = string.IsNullOrWhiteSpace(player.DisplayName) ? "玩家" : player.DisplayName;
            row.Kills.text = player.Kills.Value.ToString();
            row.Deaths.text = player.Deaths.Value.ToString();
            row.HeadshotRate.text = Mathf.RoundToInt(player.HeadshotRate).ToString();
        }
    }

    private static void HideAllRows(TeamSection section)
    {
        if (section == null)
            return;
        for (int i = 0; i < section.Rows.Count; i++)
            section.Rows[i].Root.SetActive(false);
    }
}
