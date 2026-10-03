using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>绑定用户制作的 GamePanel，并补充退出对局入口。</summary>
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
    private Text m_ScopeDescription;
    private Text m_WeaponName;
    private Image m_WeaponIcon;
    private PvpPlayerCombat m_LocalPlayer;
    private int m_LastDisplayedBullets = -1;
    private float m_NextRosterRefreshTime;
    private Button m_LeaveMatchButton;
    private Text m_SettlementText;
    private int m_LastSettlementSeconds = -1;
    private LobbyTeam m_LastSettlementWinner;
    private bool m_LastSettlementUnavailable;

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
        m_ScopeDescription = GetText("WeaponPanel/ScopeMagnification");
        m_WeaponIcon = GetImage("WeaponPanel/WeaponIcon");
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
        CreateLeaveMatchButton();
        CreateSettlementText();
    }

    private void Update()
    {
        UpdateScore();
        UpdateSettlement();

        if (m_LocalPlayer == null || Time.unscaledTime >= m_NextRosterRefreshTime)
        {
            RefreshTeamRoster();
            m_NextRosterRefreshTime = Time.unscaledTime + 0.25f;
        }

        SetSelfHealth(m_LocalPlayer);
        UpdateTeammateRows();
        UpdateRemainingBullets();
        if (m_LeaveMatchButton != null)
            m_LeaveMatchButton.interactable = Cursor.lockState != CursorLockMode.Locked &&
                LobbyManager.Instance != null && !LobbyManager.Instance.IsReturningToLobby;
    }

    private void CreateLeaveMatchButton()
    {
        var root = new GameObject("LeavePvpMatch", typeof(RectTransform), typeof(Image), typeof(Button));
        root.transform.SetParent(transform, false);
        var rect = root.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.one;
        rect.anchoredPosition = new Vector2(-24f, -24f);
        rect.sizeDelta = new Vector2(238f, 44f);
        var background = root.GetComponent<Image>();
        background.color = new Color(0.075f, 0.1f, 0.15f, 0.92f);
        m_LeaveMatchButton = root.GetComponent<Button>();
        m_LeaveMatchButton.targetGraphic = background;
        m_LeaveMatchButton.interactable = false;
        var textObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
        textObject.transform.SetParent(root.transform, false);
        var labelRect = textObject.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;
        var label = textObject.GetComponent<Text>();
        label.font = m_SelfHealthNumber.font;
        label.fontSize = 18;
        label.alignment = TextAnchor.MiddleCenter;
        label.color = Color.white;
        label.raycastTarget = false;
        label.text = "退出对局（Esc 解锁鼠标）";
        m_LeaveMatchButton.onClick.AddListener(() =>
        {
            if (LobbyManager.Instance == null || LobbyManager.Instance.IsReturningToLobby) return;
            m_LeaveMatchButton.interactable = false;
            label.text = "正在返回大厅…";
            LobbyManager.Instance.LeaveRoom();
        });
    }

    private void CreateSettlementText()
    {
        m_SettlementText = GetText("SettlementPanel/ResultText");
        if (m_SettlementText == null)
        {
            var root = new GameObject("PvpSettlementResult", typeof(RectTransform), typeof(Text), typeof(Outline));
            root.transform.SetParent(transform, false);
            var rect = root.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -112f);
            rect.sizeDelta = new Vector2(780f, 90f);
            m_SettlementText = root.GetComponent<Text>();
            m_SettlementText.font = m_SelfHealthNumber.font;
            m_SettlementText.fontSize = 26;
            m_SettlementText.alignment = TextAnchor.MiddleCenter;
            m_SettlementText.color = Color.white;
            m_SettlementText.raycastTarget = false;
            root.GetComponent<Outline>().effectColor = new Color(0f, 0f, 0f, 0.85f);
        }
        m_SettlementText.gameObject.SetActive(false);
    }

    private void UpdateSettlement()
    {
        if (m_SettlementText == null) return;
        LobbyState lobby = LobbyManager.Instance != null ? LobbyManager.Instance.CurrentLobby : null;
        bool hasResult = lobby != null && lobby.GameMode == LobbyGameMode.PVP && lobby.IsPvpMatchOver;
        m_SettlementText.gameObject.SetActive(hasResult);
        if (!hasResult)
        {
            m_LastSettlementWinner = LobbyTeam.None;
            m_LastSettlementSeconds = -1;
            return;
        }
        int seconds = lobby.GetPvpReturnSecondsRemaining();
        bool unavailable = lobby.PvpReturnToLobbyAtServerTime.Value < 0d;
        if (m_LastSettlementWinner == lobby.WinnerTeam && m_LastSettlementSeconds == seconds &&
            m_LastSettlementUnavailable == unavailable)
            return;
        m_LastSettlementWinner = lobby.WinnerTeam;
        m_LastSettlementSeconds = seconds;
        m_LastSettlementUnavailable = unavailable;
        string result = (lobby.WinnerTeam == LobbyTeam.Red ? "红队获胜" : "蓝队获胜") +
                        "   " + lobby.RedKills.Value + " : " + lobby.BlueKills.Value;
        string destination = unavailable ? "无法返回大厅，请检查 GameLobby 场景配置。" :
            seconds > 0 ? seconds + " 秒后全员返回大厅（保留原房间）" : "正在返回大厅…";
        m_SettlementText.text = result + "\n" + destination;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
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

        var loadout = m_LocalPlayer.GetComponent<PvpLoadoutCombat>();
        if (loadout != null)
        {
            string text = loadout.IsReloading ? "换弹中" : loadout.CurrentAmmo + " / " + loadout.ReserveAmmo;
            if (m_RemainingBullets.text != text) m_RemainingBullets.text = text;
            var rig = m_LocalPlayer.GetComponent<PvpWeaponRig>();
            var definition = loadout.ActiveDefinition;
            if (m_ScopeDescription != null && definition != null && rig != null)
            {
                var sight = definition.GetSight(rig.ActiveSightId);
                m_ScopeDescription.text = sight != null ? sight.displayName : "铁瞄";
            }
            if (m_WeaponIcon != null && m_WeaponName == null)
            {
                // The authored panel has a fixed AK silhouette. Use the actual equipped name in its existing rectangle.
                m_WeaponIcon.enabled = false;
                var labelObject = new GameObject("EquippedWeaponName", typeof(RectTransform), typeof(Text));
                labelObject.transform.SetParent(m_WeaponIcon.transform, false);
                m_WeaponName = labelObject.GetComponent<Text>();
                m_WeaponName.rectTransform.anchorMin = Vector2.zero;
                m_WeaponName.rectTransform.anchorMax = Vector2.one;
                m_WeaponName.rectTransform.offsetMin = m_WeaponName.rectTransform.offsetMax = Vector2.zero;
                m_WeaponName.font = m_RemainingBullets.font;
                m_WeaponName.fontSize = 24;
                m_WeaponName.color = Color.white;
                m_WeaponName.alignment = TextAnchor.MiddleCenter;
                m_WeaponName.raycastTarget = false;
            }
            if (m_WeaponName != null && definition != null)
                m_WeaponName.text = definition.weaponId == 0 ? "突击步枪" : definition.weaponId == 1 ? "手枪" : "狙击枪";
            return;
        }

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
