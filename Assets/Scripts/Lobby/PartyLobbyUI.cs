using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Bindings for the authored five-player squad and RoomPanel, separate from the legacy custom-match menu.</summary>
public sealed class PartyLobbyUI : MonoBehaviour
{
    public GameObject partyPanel;
    public GameObject roomPanel;
    public GameObject homeCharacter;
    public Button primaryButton, leaveButton, returnButton, closeRoomsButton, refreshRoomsButton;
    public Text primaryLabel, leaveLabel;
    public TMP_Text primaryTmpLabel, leaveTmpLabel, title, mapName, status, emptyRooms;
    public Transform roomListContainer;
    public RoomEntryUI roomEntryPrefab;
    public PartyMemberSlot[] members = new PartyMemberSlot[5];
    public PlayerAvatarCatalog avatarCatalog;
    public PlayerAvatarView homeAvatar;
    public MultiplayerLobbyUI home;
    private PartyLobbyClient client;
    private bool showingParty, showingRooms, wasInParty;
    private readonly Dictionary<string, RoomEntryUI> rows = new Dictionary<string, RoomEntryUI>();

    private void Awake()
    {
        if (primaryButton != null) primaryButton.onClick.AddListener(Primary);
        if (leaveButton != null) leaveButton.onClick.AddListener(Leave);
        if (returnButton != null) returnButton.onClick.AddListener(Leave);
        if (closeRoomsButton != null) closeRoomsButton.onClick.AddListener(CloseRooms);
        if (refreshRoomsButton != null) refreshRoomsButton.onClick.AddListener(RefreshRooms);
    }
    private void OnEnable()
    {
        client = PartyLobbyClient.EnsureInstance(); client.Changed += Refresh;
        AccountClient.EnsureInstance().ProfileChanged += ProfileChanged;
        Refresh();
    }
    private void Start()
    {
        if (client.Party != null) ShowParty(); else { SetVisible(false, false); }
    }
    private void OnDisable()
    {
        if (client != null) { client.Changed -= Refresh; client.BrowseRooms = false; }
        if (AccountClient.Instance != null) AccountClient.Instance.ProfileChanged -= ProfileChanged;
    }
    private void OnDestroy()
    {
        if (primaryButton != null) primaryButton.onClick.RemoveListener(Primary);
        if (leaveButton != null) leaveButton.onClick.RemoveListener(Leave);
        if (returnButton != null) returnButton.onClick.RemoveListener(Leave);
        if (closeRoomsButton != null) closeRoomsButton.onClick.RemoveListener(CloseRooms);
        if (refreshRoomsButton != null) refreshRoomsButton.onClick.RemoveListener(RefreshRooms);
    }

    public bool ValidateParty(out string error)
    {
        error = partyPanel == null || primaryButton == null || leaveButton == null || returnButton == null
            ? "组队界面尚未绑定面板、开始、离开或返回按钮，请完成 PartyLobbyUI 接线。" : "";
        return error == "";
    }
    public void CreateAndShow()
    {
        if (!ValidateParty(out _) || client.IsBusy) return;
        if (client.Party != null) { ShowParty(); return; }
        // Opening a panel does not create a party. Keep the home visible until
        // the service confirms one, and leave a failed request available to retry.
        client.Create(success =>
        {
            if (this == null) return;
            if (success && client.Party != null) ShowParty();
            else if (home != null) { home.ShowHome(); home.DisplayStatus(client.Status); }
        });
    }
    public void ShowParty() => SetVisible(true, false);
    public void OpenRooms()
    {
        if (roomPanel == null || roomListContainer == null || roomEntryPrefab == null)
        { if (home != null) home.DisplayStatus("房间列表还需要列表容器和 RoomEntryUI 预制体，请完成 PartyLobbyUI 接线。"); return; }
        SetVisible(false, true); client.BrowseRooms = true; client.RefreshRooms();
    }
    public void Hide() => SetVisible(false, false);
    private void CloseRooms() { Hide(); if (home != null) home.ShowHome(); }
    private void RefreshRooms() => client.RefreshRooms();
    private void Primary()
    {
        if (client.Party == null) return;
        if (client.IsOwner) { if (client.Party.state == "Matching") client.CancelMatching(); else client.StartMatching(); }
        else client.ToggleReady();
    }
    private void Leave()
    {
        client.Leave(success => { if (this == null || !success) return; Hide(); if (home != null) home.ShowHome(); });
    }
    private void SetVisible(bool party, bool rooms)
    {
        showingParty = party; showingRooms = rooms;
        if (partyPanel != null) partyPanel.SetActive(party);
        if (roomPanel != null) roomPanel.SetActive(rooms);
        if (homeCharacter != null) homeCharacter.SetActive(!party);
        if (client != null) client.BrowseRooms = rooms;
        Refresh();
    }
    private void ProfileChanged(PlayerProfileData profile) => Refresh();
    private void Refresh()
    {
        if (client == null) return;
        PartyData party = client.Party;
        if (party != null && !wasInParty)
        { wasInParty = true; showingParty = true; showingRooms = false; if (home != null) home.HideHomeForParty(); }
        else if (party == null && wasInParty)
        { wasInParty = false; showingParty = false; showingRooms = false; if (home != null) home.ShowHome(); }
        if (partyPanel != null) partyPanel.SetActive(showingParty);
        if (roomPanel != null) roomPanel.SetActive(showingRooms);
        if (homeCharacter != null) homeCharacter.SetActive(!showingParty);
        client.BrowseRooms = showingRooms;
        bool waiting = party != null && party.state == "Waiting";
        bool assigned = party != null && party.state == "Matched";
        if (primaryButton != null) primaryButton.interactable = !client.IsBusy && party != null && !assigned
            && (!client.IsOwner || !waiting || AllReady(party));
        if (leaveButton != null) leaveButton.interactable = !client.IsBusy;
        if (returnButton != null) returnButton.interactable = !client.IsBusy;
        string primary = client.IsOwner ? party != null && party.state == "Matching" ? "取消匹配" : "开始匹配" : client.LocalReady ? "取消准备" : "准备";
        string leave = client.IsOwner ? "解散队伍" : "离开队伍";
        if (primaryLabel != null) primaryLabel.text = primary;
        if (primaryTmpLabel != null) primaryTmpLabel.text = primary;
        if (leaveLabel != null) leaveLabel.text = leave;
        if (leaveTmpLabel != null) leaveTmpLabel.text = leave;
        if (title != null) title.text = "团队对抗";
        if (mapName != null) mapName.text = party != null ? party.mapName : "测试地图1";
        if (status != null) status.text = client.Status;
        for (int i = 0; i < members.Length; i++)
            if (members[i] != null) members[i].Show(showingParty && party != null && party.members != null && i < party.members.Length ? party.members[i] : null,
                party != null ? party.ownerAccountId : "", avatarCatalog);
        if (homeAvatar != null)
        {
            PlayerProfileData profile = AccountClient.Instance != null ? AccountClient.Instance.Profile : null;
            homeAvatar.Show(profile != null ? profile.avatarId : (byte)255, avatarCatalog);
        }
        if (showingRooms) RefreshRows();
        if (showingParty && party == null && !client.IsBusy) { showingParty = false; if (partyPanel != null) partyPanel.SetActive(false); if (home != null) home.ShowHome(); }
    }
    private static bool AllReady(PartyData party)
    { foreach (PartyMemberData member in party.members) if (!member.ready) return false; return true; }
    private void RefreshRows()
    {
        if (roomListContainer == null || roomEntryPrefab == null) return;
        var alive = new HashSet<string>();
        for (int i = 0; i < client.Rooms.Length; i++)
        {
            PartyData room = client.Rooms[i]; alive.Add(room.id);
            if (!rows.TryGetValue(room.id, out RoomEntryUI row))
            { row = Instantiate(roomEntryPrefab, roomListContainer); rows.Add(room.id, row); }
            string id = room.id;
            row.Show(room, avatarCatalog, !client.IsBusy && client.Party == null, () => client.Join(id));
            row.transform.SetSiblingIndex(i);
        }
        var removed = new List<string>();
        foreach (var row in rows) if (!alive.Contains(row.Key)) { Destroy(row.Value.gameObject); removed.Add(row.Key); }
        foreach (string id in removed) rows.Remove(id);
        if (emptyRooms != null) { emptyRooms.gameObject.SetActive(alive.Count == 0); emptyRooms.text = "暂无已创建的队伍"; }
    }
}
