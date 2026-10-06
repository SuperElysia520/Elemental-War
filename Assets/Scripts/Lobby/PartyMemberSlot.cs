using TMPro;
using UnityEngine;

/// <summary>One authored 3D preview/name/avatar slot, populated from the server-approved member profile.</summary>
public sealed class PartyMemberSlot : MonoBehaviour
{
    public GameObject occupiedRoot;
    public GameObject emptyRoot;
    public TMP_Text playerName;
    public TMP_Text readyState;
    public GameObject ownerBadge;
    public PlayerAvatarView avatar;
    public RifleGirlAppearance appearance;
    public void Show(PartyMemberData member, string ownerId, PlayerAvatarCatalog catalog)
    {
        bool occupied = member != null && member.profile != null;
        if (occupiedRoot != null) occupiedRoot.SetActive(occupied);
        if (emptyRoot != null) emptyRoot.SetActive(!occupied);
        if (appearance != null) appearance.gameObject.SetActive(occupied);
        if (playerName != null) playerName.text = occupied ? member.profile.username : "";
        bool owner = occupied && member.profile.accountId == ownerId;
        if (ownerBadge != null) ownerBadge.SetActive(owner);
        if (readyState != null)
        {
            readyState.text = occupied ? owner ? "房主" : member.ready ? "已准备" : "未准备" : "空位";
            readyState.color = owner ? Color.yellow : occupied && member.ready ? new Color(0.2f, 1f, 0.4f) : Color.white;
        }
        if (avatar != null) avatar.Show(occupied ? member.profile.avatarId : (byte)255, catalog);
        if (occupied && appearance != null) { appearance.SetLocalFirstPerson(false); appearance.Apply(member.profile.appearance); }
    }
}
