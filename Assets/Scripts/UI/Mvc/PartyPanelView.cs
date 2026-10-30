using TMPro;
using UnityEngine;

namespace FPS.UI
{
    public sealed class PartyPanelView : LobbyPageView
    {
        public UnityEngine.UI.Button primaryButton;
        public UnityEngine.UI.Text primaryLabel, leaveLabel;
        public TMP_Text primaryTmpLabel, leaveTmpLabel, title, mapName;
        public PartyMemberSlot[] members = new PartyMemberSlot[0];
        public void RenderParty(PartyData party, bool owner, bool ready, bool canStart, PlayerAvatarCatalog catalog, bool busy)
        {
            string primary = owner ? party != null && party.state == "Matching" ? "取消匹配" : "开始匹配" : ready ? "取消准备" : "准备";
            if (primaryLabel != null) primaryLabel.text = primary;
            if (primaryTmpLabel != null) primaryTmpLabel.text = primary;
            if (leaveLabel != null) leaveLabel.text = owner ? "解散队伍" : "离开队伍";
            if (leaveTmpLabel != null) leaveTmpLabel.text = owner ? "解散队伍" : "离开队伍";
            if (title != null) title.text = "团队对抗";
            if (mapName != null) mapName.text = party != null ? party.mapName : "测试地图1";
            if (primaryButton != null) primaryButton.interactable = !busy && canStart;
            for (int i = 0; i < members.Length; i++)
                if (members[i] != null) members[i].Show(party != null && party.members != null && i < party.members.Length ? party.members[i] : null,
                    party != null ? party.ownerAccountId : "", catalog);
        }
    }
}
