using TMPro;
using UnityEngine;

namespace FPS.UI
{
    /// <summary>One passive homepage for both solo matchmaking and a five-person party.</summary>
    public sealed class UnifiedLobbyView : LobbyPageView
    {
        public UnityEngine.UI.Button primaryButton, roomsButton, leaveButton, createPartyButton;
        public GameObject squadCard, matchCard;
        public TMP_Text primaryLabel, leaveLabel, teamCount, mapName;
        public PlayerAvatarView[] memberAvatars = new PlayerAvatarView[0];
        public GameObject[] occupiedSlots = new GameObject[0], emptySlots = new GameObject[0];
        public TMP_Text[] memberNames = new TMP_Text[0], memberStates = new TMP_Text[0];

        public void RenderParty(PlayerProfileData profile, PartyData party, bool owner, bool ready, bool canStart, PlayerAvatarCatalog catalog, bool busy)
        {
            bool hasParty = party != null && !string.IsNullOrEmpty(party.id);
            if (!hasParty) party = null;
            if (squadCard != null) squadCard.SetActive(hasParty);
            if (createPartyButton != null) createPartyButton.interactable = !busy && !hasParty;
            int count = party?.members?.Length ?? 0;
            teamCount.text = "队伍  " + count + "/5";
            mapName.text = party != null && !string.IsNullOrEmpty(party.mapName) ? party.mapName : "团队竞技 · 5V5";
            bool matching = party != null && party.state == "Matching";
            bool matched = party != null && party.state == "Matched";
            primaryLabel.text = matched ? "正在进入对局…" : party == null ? "开始匹配" : owner ? matching ? "取消匹配" : canStart ? "开始匹配" : "等待队员准备" : ready ? "取消准备" : "准备";
            primaryButton.interactable = !busy && !matched && (party == null || canStart);
            roomsButton.interactable = !busy && party == null;
            leaveButton.gameObject.SetActive(party != null);
            leaveButton.interactable = !busy && !matched;
            leaveLabel.text = owner ? "解散队伍" : "离开队伍";
            for (int i = 0; i < memberAvatars.Length; i++)
            {
                var member = party?.members != null && i < party.members.Length ? party.members[i] : null;
                var current = member?.profile;
                bool occupied = current != null;
                occupiedSlots[i].SetActive(occupied); emptySlots[i].SetActive(!occupied);
                memberAvatars[i].Show(occupied ? current.avatarId : (byte)255, catalog);
                memberNames[i].text = occupied ? current.DisplayName : "";
                memberStates[i].text = !occupied ? "" : current.accountId == party.ownerAccountId ? "队长" : member.ready ? "已准备" : "未准备";
            }
        }
    }
}
