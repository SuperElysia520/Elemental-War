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
        public UnityEngine.UI.Button firstPageButton, informationButton, editNameButton, confirmNameButton, closeNameButton;
        public GameObject nameEditor;
        public UnityEngine.UI.InputField nameInput;
        public TMPro.TMP_InputField signatureInput;
        public UnityEngine.UI.Toggle maleToggle, femaleToggle;
        public UnityEngine.UI.Image genderIcon;
        public Sprite maleIcon, femaleIcon;
        public UnityEngine.UI.ScrollRect avatarScroll;
        public UnityEngine.UI.Text matchesText, winsText, killsText, kdText, deathsText, headshotRateText;
        public Color selectedTabColor = new Color(1f, 0.84f, 0f, 1f);
        private Color firstPageColor, informationColor;
        private PlayerProfileData currentProfile;
        private bool personalPending, signatureDirty;
        private UnityAction[] choiceActions;
        public event Action<byte> AvatarRequested;
        public event Action<PersonalProfileData> PersonalRequested;
        protected override void Awake()
        {
            base.Awake(); choiceActions = new UnityAction[choiceButtons.Length];
            for (int i = 0; i < choiceButtons.Length; i++)
            {
                byte id = (byte)i; choiceActions[i] = () => AvatarRequested?.Invoke(id);
                if (choiceButtons[i] != null) choiceButtons[i].onClick.AddListener(choiceActions[i]);
            }
            firstPageColor = firstPageButton != null ? firstPageButton.image.color : Color.white;
            informationColor = informationButton != null ? informationButton.image.color : Color.white;
            if (editNameButton != null) editNameButton.onClick.AddListener(OpenNameEditor);
            if (confirmNameButton != null) confirmNameButton.onClick.AddListener(SaveName);
            if (closeNameButton != null) closeNameButton.onClick.AddListener(CancelNameEditor);
            if (maleToggle != null) maleToggle.onValueChanged.AddListener(SelectMale);
            if (femaleToggle != null) femaleToggle.onValueChanged.AddListener(SelectFemale);
            if (signatureInput != null) signatureInput.onEndEdit.AddListener(SaveSignature);
        }
        protected override void OnDestroy()
        {
            if (choiceActions != null) for (int i = 0; i < choiceButtons.Length; i++)
                if (choiceButtons[i] != null) choiceButtons[i].onClick.RemoveListener(choiceActions[i]);
            if (editNameButton != null) editNameButton.onClick.RemoveListener(OpenNameEditor);
            if (confirmNameButton != null) confirmNameButton.onClick.RemoveListener(SaveName);
            if (closeNameButton != null) closeNameButton.onClick.RemoveListener(CancelNameEditor);
            if (maleToggle != null) maleToggle.onValueChanged.RemoveListener(SelectMale);
            if (femaleToggle != null) femaleToggle.onValueChanged.RemoveListener(SelectFemale);
            if (signatureInput != null) signatureInput.onEndEdit.RemoveListener(SaveSignature);
            base.OnDestroy();
        }
        public void ShowTab(bool showInformation, bool showAvatars, PlayerAvatarCatalog catalog)
        {
            bool openingPicker = showInformation && showAvatars && !avatarPicker.activeSelf;
            if (!showInformation || showAvatars) CloseNameEditor();
            overview.SetActive(!showInformation); information.SetActive(showInformation); avatarPicker.SetActive(showInformation && showAvatars);
            SetTabColor(firstPageButton, showInformation ? firstPageColor : selectedTabColor);
            SetTabColor(informationButton, showInformation ? selectedTabColor : informationColor);
            for (int i = 0; i < avatarChoices.Length; i++)
            {
                if (avatarChoices[i] != null) avatarChoices[i].Show((byte)i, catalog);
                if (i < choiceButtons.Length && choiceButtons[i] != null) choiceButtons[i].interactable = catalog != null && catalog.Get((byte)i) != null;
            }
            if (openingPicker && avatarScroll != null)
            { UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(avatarScroll.content); avatarScroll.StopMovement(); avatarScroll.verticalNormalizedPosition = 1f; }
        }
        public override void Render(PlayerProfileData profile, PlayerAvatarCatalog catalog, string message, bool busy)
        {
            if (currentProfile?.accountId != profile?.accountId) signatureDirty = false;
            currentProfile = profile;
            base.Render(profile, catalog, message, busy);
            if (overviewName != null) overviewName.text = profile != null ? profile.DisplayName : "";
            if (overviewUid != null) overviewUid.text = profile != null ? AccountUid.Format(profile.uid, profile.accountId) : "";
            if (overviewAvatar != null) overviewAvatar.Show(profile != null ? profile.avatarId : (byte)255, catalog);
            byte gender = profile?.personal?.gender ?? 0;
            if (genderIcon != null) { genderIcon.sprite = gender == 1 ? maleIcon : gender == 2 ? femaleIcon : null; genderIcon.enabled = genderIcon.sprite != null; }
            if (!personalPending)
            {
                if (maleToggle != null) maleToggle.SetIsOnWithoutNotify(gender == 1);
                if (femaleToggle != null) femaleToggle.SetIsOnWithoutNotify(gender == 2);
                if (signatureInput != null && !signatureInput.isFocused && !signatureDirty) signatureInput.SetTextWithoutNotify(profile?.personal?.signature ?? "");
            }
            RenderCareer(profile?.career ?? new PlayerCareerRecord());
        }

        public override void Show(bool visible)
        {
            if (!visible) CloseNameEditor();
            base.Show(visible);
        }

        private static void SetTabColor(UnityEngine.UI.Button button, Color color)
        {
            if (button == null || button.image == null) return;
            // Tint the Image itself; Selectable still handles hover, pressed and keyboard focus.
            button.image.color = color;
        }
        private void OpenNameEditor()
        {
            if (nameEditor == null || currentProfile == null) return;
            nameInput.SetTextWithoutNotify(currentProfile.DisplayName);
            nameEditor.SetActive(true); nameInput.Select(); nameInput.ActivateInputField();
        }
        private void CancelNameEditor() { CloseNameEditor(); }
        public bool CloseNameEditor()
        { if (nameEditor == null || !nameEditor.activeSelf) return false; nameEditor.SetActive(false); return true; }
        private PersonalProfileData Personal() => new PersonalProfileData
        {
            displayName = currentProfile?.personal?.displayName ?? "",
            gender = currentProfile?.personal?.gender ?? 0,
            signature = currentProfile?.personal?.signature ?? ""
        };
        private void SaveName()
        {
            if (personalPending || currentProfile == null || string.IsNullOrWhiteSpace(nameInput.text)) return;
            var value = Personal(); value.displayName = nameInput.text.Trim(); Submit(value);
        }
        private void SelectMale(bool selected)
        { if (selected) { femaleToggle.SetIsOnWithoutNotify(false); SaveGender(1); } else if ((currentProfile?.personal?.gender ?? 0) == 1) maleToggle.SetIsOnWithoutNotify(true); }
        private void SelectFemale(bool selected)
        { if (selected) { maleToggle.SetIsOnWithoutNotify(false); SaveGender(2); } else if ((currentProfile?.personal?.gender ?? 0) == 2) femaleToggle.SetIsOnWithoutNotify(true); }
        private void SaveGender(byte gender)
        {
            if (personalPending || currentProfile == null || gender == (currentProfile.personal?.gender ?? 0)) return;
            var value = Personal(); value.gender = gender; Submit(value);
        }
        private void SaveSignature(string text)
        {
            if (personalPending || currentProfile == null || text == (currentProfile.personal?.signature ?? "")) return;
            signatureDirty = true; var value = Personal(); value.signature = text; Submit(value);
        }
        private void Submit(PersonalProfileData value)
        { if (PersonalRequested == null) return; personalPending = true; PersonalRequested.Invoke(value); }
        public void PersonalSaveCompleted(bool success)
        {
            personalPending = false;
            if (success) { signatureDirty = false; CloseNameEditor(); }
            if (!success && signatureInput != null) signatureInput.ActivateInputField();
        }
        private void RenderCareer(PlayerCareerRecord career)
        {
            if (matchesText != null) matchesText.text = career.matches.ToString();
            if (winsText != null) winsText.text = career.wins.ToString();
            if (killsText != null) killsText.text = career.kills.ToString();
            if (kdText != null) kdText.text = (career.kills / (float)Mathf.Max(1, career.deaths)).ToString("0.00");
            if (deathsText != null) deathsText.text = career.deaths.ToString();
            if (headshotRateText != null) headshotRateText.text = (career.kills > 0 ? career.headshotKills * 100f / career.kills : 0f).ToString("0.0") + "%";
        }
    }
}
