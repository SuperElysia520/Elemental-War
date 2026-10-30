using System;
using UnityEngine;
using UnityEngine.Events;

namespace FPS.UI
{
    public sealed class ProfilePanelView : LobbyPageView
    {
        public GameObject overview, information, avatarPicker;
        public PlayerAvatarView[] avatarChoices = new PlayerAvatarView[0];
        public UnityEngine.UI.Button[] choiceButtons = new UnityEngine.UI.Button[0];
        public TMPro.TMP_Text overviewName, overviewUid;
        public PlayerAvatarView overviewAvatar;
        private UnityAction[] choiceActions;
        public event Action<byte> AvatarRequested;
        protected override void Awake()
        {
            base.Awake(); choiceActions = new UnityAction[choiceButtons.Length];
            for (int i = 0; i < choiceButtons.Length; i++)
            {
                byte id = (byte)i; choiceActions[i] = () => AvatarRequested?.Invoke(id);
                choiceButtons[i].onClick.AddListener(choiceActions[i]);
            }
        }
        protected override void OnDestroy()
        {
            if (choiceActions != null) for (int i = 0; i < choiceButtons.Length; i++) choiceButtons[i].onClick.RemoveListener(choiceActions[i]);
            base.OnDestroy();
        }
        public void ShowTab(bool showInformation, bool showAvatars, PlayerAvatarCatalog catalog)
        {
            overview.SetActive(!showInformation); information.SetActive(showInformation); avatarPicker.SetActive(showInformation && showAvatars);
            for (int i = 0; i < avatarChoices.Length; i++)
            { avatarChoices[i].Show((byte)i, catalog); choiceButtons[i].interactable = catalog != null && catalog.Get((byte)i) != null; }
        }
        public override void Render(PlayerProfileData profile, PlayerAvatarCatalog catalog, string message, bool busy)
        {
            base.Render(profile, catalog, message, busy);
            if (overviewName != null) overviewName.text = profile != null ? profile.username : "";
            if (overviewUid != null) overviewUid.text = profile != null ? profile.accountId : "";
            if (overviewAvatar != null) overviewAvatar.Show(profile != null ? profile.avatarId : (byte)255, catalog);
        }
    }
}
