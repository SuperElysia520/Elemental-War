using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Data binding for the user's RoomEntry prefab; no layout is created at runtime.</summary>
public sealed class RoomEntryUI : MonoBehaviour
{
    public TMP_Text roomName;
    public TMP_Text mapName;
    public TMP_Text roomState;
    public Button joinButton;
    public PlayerAvatarView[] avatars = new PlayerAvatarView[5];
    private Action join;
    private void Awake() { if (joinButton != null) joinButton.onClick.AddListener(Join); }
    private void OnDestroy() { if (joinButton != null) joinButton.onClick.RemoveListener(Join); }
    private void Join() => join?.Invoke();
    public void Show(PartyData party, PlayerAvatarCatalog catalog, bool canJoin, Action onJoin)
    {
        join = onJoin;
        if (roomName != null) roomName.text = "团队对抗";
        if (mapName != null) mapName.text = party.mapName;
        int count = party.members != null ? party.members.Length : 0;
        if (roomState != null) roomState.text = party.state == "Matching" ? "匹配中" : count >= 5 ? "已满" : count + "/5";
        if (joinButton != null) joinButton.interactable = canJoin && count < 5 && party.state == "Waiting";
        for (int i = 0; i < avatars.Length; i++)
            if (avatars[i] != null) avatars[i].Show(i < count ? party.members[i].profile.avatarId : (byte)255, catalog);
    }
}
