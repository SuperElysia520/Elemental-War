using System;
using System.Collections;
using UnityEngine;

namespace FPS.UI
{
    /// <summary>GameLobby composition root. Scene references end here; panel prefabs reference only their own children and assets.</summary>
    public sealed class LobbyUIRoot : MonoBehaviour
    {
        public LobbyPageView[] pages = new LobbyPageView[0];
        public PartyPanelView partyView;
        public RoomListView roomsView;
        public CustomRoomView customRoomView;
        public CustomizationView customizationView;
        public LobbyDialogView messageDialog, confirmDialog;
        public PlayerAvatarCatalog avatars;
        public GameObject loading;
        public GameObject[] mainEnvironment = new GameObject[0], lobbyEnvironment = new GameObject[0], mainDecorations = new GameObject[0];
        public GameObject homeCharacter;
        public GameObject mainCharacter;
        public LobbyIdentityView identity;
        public RifleGirlAppearance[] partyCharacters = new RifleGirlAppearance[0];
        private LobbyController controller;
        public LobbyController Controller => controller;
        private void Awake()
        {
            foreach (var page in pages) page.Show(false);
            messageDialog.Hide(); confirmDialog.Hide();
            if (loading != null) loading.SetActive(!DedicatedServerBootstrap.IsDedicatedServerProcess);
        }
        private IEnumerator Start()
        {
            if (DedicatedServerBootstrap.IsDedicatedServerProcess)
            {
                foreach (var sceneRoot in gameObject.scene.GetRootGameObjects())
                    foreach (var canvas in sceneRoot.GetComponentsInChildren<Canvas>(true)) canvas.gameObject.SetActive(false);
                yield break;
            }
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true; Time.timeScale = 1f;
            var loader = LoginSceneBootstrap.FindLoader(gameObject.scene);
            if (loader == null) throw new InvalidOperationException("GameLobby profile loader was not composed.");
            while (!loader.IsReady) yield return null;
            var account = AccountClient.EnsureInstance();
            var party = PartyLobbyClient.EnsureInstance();
            if (LobbyManager.Instance == null) throw new InvalidOperationException("GameLobby requires its authored network root.");
            controller = new LobbyController(this, account, party, LobbyManager.Instance);
            controller.Initialize();
            if (loading != null) loading.SetActive(false);
        }
        private void Update()
        { if (controller != null && Input.GetKeyDown(KeyCode.Escape)) controller.Back(); }
        private void OnDestroy() { controller?.Dispose(); controller = null; }
        public void Present(LobbyModel model, PlayerProfileData profile, string status, bool blocked)
        {
            bool main = model.Page == LobbyPage.MainMenu || model.Page == LobbyPage.CustomRoom || model.Page == LobbyPage.Customization;
            foreach (var item in mainEnvironment) if (item != null) item.SetActive(main);
            foreach (var item in lobbyEnvironment) if (item != null) item.SetActive(!main);
            foreach (var item in mainDecorations) if (item != null) item.SetActive(main && model.Page != LobbyPage.Customization);
            if (mainCharacter != null) mainCharacter.SetActive(main && model.Page != LobbyPage.Customization);
            if (identity != null) identity.Render(profile, !main);
            if (homeCharacter != null) homeCharacter.SetActive(model.Page != LobbyPage.Party && model.Page != LobbyPage.Customization);
            foreach (var page in pages)
            {
                page.Show(page.page == model.Page);
                if (page.page == model.Page) page.Render(profile, avatars, status, blocked);
            }
        }
        public void PresentPartyCharacters(PartyData party)
        {
            for (int i = 0; i < partyCharacters.Length; i++)
            {
                var character = partyCharacters[i];
                if (character == null) continue;
                var member = party != null && party.members != null && i < party.members.Length ? party.members[i] : null;
                bool visible = controller != null && controller.Model.Page == LobbyPage.Party && member != null && member.profile != null;
                character.gameObject.SetActive(visible);
                if (visible) { character.SetLocalFirstPerson(false); character.Apply(member.profile.appearance); }
            }
        }
    }
}
