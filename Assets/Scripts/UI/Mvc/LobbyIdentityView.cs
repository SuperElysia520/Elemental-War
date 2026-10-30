using TMPro;
using UnityEngine;

namespace FPS.UI
{
    public sealed class LobbyIdentityView : MonoBehaviour
    {
        public TMP_Text uid;
        public UnityEngine.UI.Text legacyUid;
        public void Render(PlayerProfileData profile, bool visible)
        {
            gameObject.SetActive(visible);
            if (uid != null) uid.text = profile != null ? "UID: " + profile.accountId : "";
            if (legacyUid != null) legacyUid.text = profile != null ? "UID: " + profile.accountId : "";
        }
    }
}
