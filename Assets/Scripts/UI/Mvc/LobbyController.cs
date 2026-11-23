using System;
using System.Collections;
using Unity.Netcode;
using UnityEngine;

namespace FPS.UI
{
    /// <summary>Owns navigation and user operations, with services injected by LobbyUIRoot.</summary>
    public sealed class LobbyController : IDisposable
    {
        private readonly LobbyUIRoot view;
        private readonly AccountClient account;
        private readonly PartyLobbyClient party;
        private readonly LobbyManager lobby;
        private readonly CustomizationModel draft = new CustomizationModel();
        private readonly CustomRoomController custom;
        private bool disposed, modal, observedParty, returningToLogin;
        private Action confirmed;
        private string draftMessage = "";
        public LobbyModel Model { get; } = new LobbyModel();
        public CustomizationModel Draft => draft;
        public bool IsModalOpen => modal;
        private bool Busy => account.IsBusy || party.IsBusy || Model.IsBusy;
        public LobbyController(LobbyUIRoot view, AccountClient account, PartyLobbyClient party, LobbyManager lobby)
        {
            this.view = view; this.account = account; this.party = party; this.lobby = lobby;
            custom = new CustomRoomController(view, lobby, Model, Refresh);
        }
        public void Initialize()
        {
            foreach (var page in view.pages) page.ActionRequested += Execute;
            if (view.Wardrobe != null) { view.Wardrobe.Changed += Dress; view.Wardrobe.UndoRequested += UndoDress; }
            view.roomsView.JoinRequested += Join;
            view.customizationView.ChoiceRequested += ChangeChoice;
            foreach (var page in view.pages) if (page is ProfilePanelView profile)
            { profile.AvatarRequested += SaveAvatar; profile.PersonalRequested += SavePersonal; }
            foreach (var page in view.pages) if (page is FPS.Settings.SettingsPanelView settings)
                settings.CloseRequested += SettingsClosed;
            view.messageDialog.Confirmed += CloseDialog; view.messageDialog.Cancelled += CloseDialog;
            view.confirmDialog.Confirmed += Confirm; view.confirmDialog.Cancelled += CloseDialog;
            account.StateChanged += AccountChanged; account.ProfileChanged += ProfileChanged;
            party.Changed += PartyChanged; lobby.OnLobbyChanged += LobbyChanged; Model.Changed += NavigationChanged;
            custom.Initialize();
            draft.Load(account.Profile); observedParty = party.Party != null;
            if (lobby.IsInLobby && (lobby.CurrentLobby == null || !lobby.CurrentLobby.IsMatchmaking))
                custom.Open(lobby.CurrentLobby != null ? lobby.CurrentLobby.GameMode : LobbyGameMode.PVP);
            Model.Reset(lobby.IsInLobby && (lobby.CurrentLobby == null || !lobby.CurrentLobby.IsMatchmaking) ? LobbyPage.CustomRoom : LobbyPage.Home);
        }
        public void Execute(LobbyAction action)
        {
            if (disposed || modal || Busy) return;
            Model.Status = "";
            switch (action)
            {
                case LobbyAction.Back: Back(); break;
                case LobbyAction.Settings: Model.Navigate(LobbyPage.Settings); break;
                case LobbyAction.Friends: view.Social?.Open(false); break;
                case LobbyAction.Chat: view.Social?.Open(true); break;
                case LobbyAction.Multiplayer: Model.Reset(LobbyPage.Home); break;
                // Pve retains its serialized ordinal but is no longer a menu feature.
                case LobbyAction.Pve: break;
                case LobbyAction.Training: Message("训练靶场尚未开放。"); break;
                case LobbyAction.CustomPvp: OpenCustom(LobbyGameMode.PVP); break;
                case LobbyAction.Character: break;
                case LobbyAction.Clothes:
                    if (!CanEdit(out string dressReason)) { Message(dressReason); break; }
                    draft.Load(account.Profile); draftMessage = ""; Model.Navigate(view.Wardrobe != null ? LobbyPage.Wardrobe : LobbyPage.Customization); break;
                case LobbyAction.Loadout:
                    if (!CanEdit(out string reason)) { Message(reason); break; }
                    draft.Load(account.Profile); draftMessage = ""; Model.Navigate(LobbyPage.Customization); break;
                case LobbyAction.Profile:
                    Model.ShowProfileInformation = false; Model.ShowAvatarPicker = false;
                    Model.Navigate(LobbyPage.Profile);
                    account.RefreshProfile((ok, message) => { if (!disposed && !ok) Message(message); }); break;
                case LobbyAction.ProfileOverview: Model.ShowProfileInformation = false; Model.ShowAvatarPicker = false; break;
                case LobbyAction.ProfileInformation: Model.ShowProfileInformation = true; Model.ShowAvatarPicker = false; break;
                case LobbyAction.Avatar: Model.ShowAvatarPicker = true; break;
                case LobbyAction.CloseAvatarPicker: Model.ShowAvatarPicker = false; break;
                case LobbyAction.CreateParty:
                    if (lobby.IsInLobby || lobby.IsConnecting) { Message("请先离开当前自定义房间。"); break; }
                    if (party.Party != null)
                    {
                        if (view.Social != null) view.Social.Open(false);
                        else Message("队伍已创建，其他玩家可通过房间列表加入。");
                    }
                    else party.Create(ok => { if (disposed) return; if (!ok) Model.Status = party.Status; Refresh(); });
                    break;
                case LobbyAction.Rooms:
                    if (party.Party != null) { Message("请先离开当前队伍再浏览其他队伍。"); break; }
                    Model.Navigate(LobbyPage.Rooms); party.RefreshRooms(); break;
                case LobbyAction.RefreshRooms: party.RefreshRooms(); break;
                case LobbyAction.Primary:
                    if (party.Party == null)
                    {
                        if (lobby.IsInLobby || lobby.IsConnecting) { Message("请先离开当前自定义房间。"); break; }
                        party.Create(ok =>
                        {
                            if (disposed) return;
                            if (!ok) Model.Status = party.Status;
                            else if (Model.Page == LobbyPage.Home && party.IsOwner && AllReady(party.Party)) party.StartMatching();
                            Refresh();
                        });
                        break;
                    }
                    if (party.IsOwner) { if (party.Party.state == "Matching") party.CancelMatching(); else if (AllReady(party.Party)) party.StartMatching(); }
                    else party.ToggleReady(); break;
                case LobbyAction.LeaveParty: LeaveParty(); break;
                case LobbyAction.SaveProfile: Save(); break;
                case LobbyAction.ReloadProfile:
                    if (draft.IsDirty) Ask("重新载入会放弃未保存的更改，是否继续？", Reload); else Reload(); break;
                case LobbyAction.Exit: Ask("确认退出游戏？", Application.Quit); break;
                case LobbyAction.Placeholder: Message("该功能尚未开放。"); break;
                default: custom.Execute(action); break;
            }
            Refresh();
        }
        public void Back()
        {
            if (disposed) return;
            if (modal) { CloseDialog(); return; }
            if (view.Social != null && view.Social.IsOpen) { view.Social.Close(); return; }
            if (Model.Page == LobbyPage.Settings)
            {
                foreach (var page in view.pages) if (page is FPS.Settings.SettingsPanelView settings) { settings.RequestClose(); return; }
            }
            if (Busy) return;
            if (Model.Page == LobbyPage.Profile)
                foreach (var page in view.pages) if (page is ProfilePanelView profile && profile.CloseNameEditor()) return;
            if (Model.Page == LobbyPage.Profile && Model.ShowAvatarPicker) { Model.ShowAvatarPicker = false; Refresh(); return; }
            if (Model.Page == LobbyPage.Home) { Ask("确认退出游戏？", Application.Quit); return; }
            if (Model.Page == LobbyPage.Party) { LeaveParty(); return; }
            if (Model.Page == LobbyPage.CustomRoom) { custom.Close(); Model.Back(); return; }
            if (Model.Page == LobbyPage.Wardrobe && view.Wardrobe.CloseDetails()) return;
            if ((Model.Page == LobbyPage.Customization || Model.Page == LobbyPage.Wardrobe) && draft.IsDirty)
            { Ask("尚有未保存的更改，是否放弃并返回？", () => { draft.Load(account.Profile); Model.Back(); }); return; }
            Model.Back();
        }
        private void SettingsClosed() { if (!disposed && Model.Page == LobbyPage.Settings) Model.Back(); }
        private void Join(string id)
        { if (!disposed && !modal && !Busy && Model.Page == LobbyPage.Rooms && party.Party == null) party.Join(id); }
        private void SaveAvatar(byte id)
        {
            if (disposed || Busy || modal || Model.Page != LobbyPage.Profile || !Model.ShowAvatarPicker) return;
            if (!CanEdit(out string reason)) { Message(reason); return; }
            if (view.avatars == null || view.avatars.Get(id) == null) return;
            account.SaveAvatar(id, (ok, message) =>
            { if (disposed) return; if (ok) Model.ShowAvatarPicker = false; else Message(message); Refresh(); });
        }
        private void SavePersonal(PersonalProfileData personal)
        {
            if (disposed || Busy || modal || Model.Page != LobbyPage.Profile)
            { foreach (var page in view.pages) if (page is ProfilePanelView profile) profile.PersonalSaveCompleted(false); return; }
            if (!CanEdit(out string reason))
            { foreach (var page in view.pages) if (page is ProfilePanelView profile) profile.PersonalSaveCompleted(false); Message(reason); return; }
            account.SavePersonalProfile(personal, (ok, message) =>
            {
                if (disposed) return;
                foreach (var page in view.pages) if (page is ProfilePanelView profile) profile.PersonalSaveCompleted(ok);
                if (!ok) Message(message);
                Refresh();
            });
            Refresh();
        }

        private void LeaveParty()
        {
            if (party.Party == null) { Model.Reset(LobbyPage.Home); return; }
            party.Leave(ok => { if (disposed) return; if (ok) Model.Reset(LobbyPage.Home); else { Model.Status = party.Status; Refresh(); } });
        }
        private void OpenCustom(LobbyGameMode mode)
        {
            if (party.Party != null) { Message("请先离开匹配队伍，再进入自定义房间。"); return; }
            if (mode == LobbyGameMode.PVE && !Application.CanStreamedLevelBeLoaded("GamePVE"))
            { Message("PVE 场景尚未加入 Build Settings，暂时无法开始 PVE 对局。"); return; }
            custom.Open(mode); Model.Navigate(LobbyPage.CustomRoom);
        }
        private bool CanEdit(out string message)
        {
            if (!account.IsAuthenticated) { message = "请先登录账号。"; return false; }
            if (party.Party != null && party.Party.state != "Waiting")
            { message = "请取消匹配后再编辑角色与配装。"; return false; }
            var state = lobby.CurrentLobby;
            if (state != null && (state.PvpIsPreparingMatch.Value || state.IsStarted.Value))
            { message = "正在确认全员配装或进入对局，请稍后再编辑。"; return false; }
            message = ""; return true;
        }
        private void Dress(CharacterAppearanceData appearance)
        {
            if (disposed || modal || Busy || Model.Page != LobbyPage.Wardrobe || !CanEdit(out _)) return;
            draft.Dress(appearance); draftMessage = ""; Refresh();
        }
        private void UndoDress()
        {
            if (disposed || modal || Busy || Model.Page != LobbyPage.Wardrobe) return;
            draft.Load(account.Profile); draftMessage = ""; Refresh();
        }
        private void ChangeChoice(int choice, int direction)
        {
            if (disposed || modal || Busy || Model.Page != LobbyPage.Customization || (view.Wardrobe != null && choice < 6) || !CanEdit(out _)) return;
            draft.Change(choice, direction); draftMessage = draft.IsDirty ? "有未保存的更改。" : ""; Refresh();
        }
        private void Save()
        {
            if (!CanEdit(out string reason)) { Message(reason); return; }
            bool dressing = Model.Page == LobbyPage.Wardrobe;
            if (dressing && !account.Profile.appearance.Equals(draft.SavedAppearance))
            { draftMessage = "穿搭已在其他位置更新，请撤销试穿后重新选择。"; Refresh(); return; }
            account.SaveProfile(dressing || view.Wardrobe == null ? draft.Appearance : account.Profile.appearance, dressing ? account.Profile.loadout : draft.Loadout, (ok, message) =>
            {
                if (disposed) return;
                if (ok) draft.Load(account.Profile);
                draftMessage = ok ? (dressing ? "搭配已保存。" : "武器配装已保存。") : message; Refresh();
            });
        }
        private void Reload()
        {
            if (disposed || Busy || !CanEdit(out _)) return;
            account.RefreshProfile((ok, message) =>
            {
                if (disposed) return;
                if (ok) draft.Load(account.Profile);
                draftMessage = ok ? "已载入最新云端档案。" : message; Refresh();
            });
        }
        private void ProfileChanged(PlayerProfileData profile)
        {
            if (disposed) return;
            // Remote/profile notifications must not overwrite a draft the player is still editing.
            if ((Model.Page != LobbyPage.Customization && Model.Page != LobbyPage.Wardrobe) || !draft.IsDirty) draft.Load(profile);
            AccountChanged();
        }
        private void AccountChanged()
        {
            if (disposed) return;
            if (!account.HasSession)
            {
                if (returningToLogin) return;
                returningToLogin = true; Model.IsBusy = true;
                lobby.LeaveRoom(); LoginSceneBootstrap.ReturnToLogin("账号会话已失效，请重新登录。"); return;
            }
            Refresh();
        }
        private void PartyChanged()
        {
            if (disposed) return;
            bool exists = party.Party != null;
            if (exists && !observedParty && Model.Page == LobbyPage.Rooms) Model.Reset(LobbyPage.Home);
            observedParty = exists; Refresh();
        }
        private void LobbyChanged()
        {
            if (disposed) return;
            if ((Model.Page == LobbyPage.MainMenu || Model.Page == LobbyPage.Home) && lobby.IsInLobby && lobby.CurrentLobby != null && !lobby.CurrentLobby.IsMatchmaking)
            { custom.Open(lobby.CurrentLobby.GameMode); Model.Reset(LobbyPage.CustomRoom); return; }
            Refresh();
        }
        private void NavigationChanged()
        {
            party.BrowseRooms = Model.Page == LobbyPage.Rooms;
            custom.SetVisible(Model.Page == LobbyPage.CustomRoom);
            Refresh();
        }
        public void Refresh()
        {
            if (disposed) return;
            string message = Busy ? party.IsBusy ? party.Party == null ? "正在创建队伍…" : "正在处理队伍请求…" : "正在同步账号…"
                : !string.IsNullOrEmpty(Model.Status) ? Model.Status : !string.IsNullOrEmpty(account.LastError) ? account.LastError
                : !string.IsNullOrEmpty(party.Status) ? party.Status : Model.Page == LobbyPage.Rooms ? "选择队伍加入，或刷新房间列表。" : "";
            view.Present(Model, account.Profile, message, Busy || modal);
            bool canStart = party.Party != null && party.Party.state != "Matched"
                && (!party.IsOwner || party.Party.state != "Waiting" || AllReady(party.Party));
            if (Model.Page == LobbyPage.Home && view.homeView != null)
                view.homeView.RenderParty(account.Profile, party.Party, party.IsOwner, party.LocalReady, canStart, view.avatars, Busy || modal);
            if (Model.Page == LobbyPage.Party && view.partyView != null)
                view.partyView.RenderParty(party.Party, party.IsOwner, party.LocalReady, canStart, view.avatars, Busy || modal);
            if (Model.Page == LobbyPage.Rooms) view.roomsView.RenderRooms(party.Rooms, view.avatars, !Busy && !modal && party.Party == null);
            if (Model.Page == LobbyPage.CustomRoom) custom.Render();
            foreach (var page in view.pages)
                if (page is ProfilePanelView profileView && Model.Page == LobbyPage.Profile) profileView.ShowTab(Model.ShowProfileInformation, Model.ShowAvatarPicker, view.avatars);
            if (Model.Page == LobbyPage.Wardrobe)
            {
                bool editable = CanEdit(out string reason);
                view.Wardrobe.RenderDraft(draft,account.Profile,view.unifiedEnvironment,editable ? draftMessage : reason,editable,Busy || modal);
            }
            if (Model.Page == LobbyPage.Customization)
            {
                bool editable = CanEdit(out string reason);
                view.customizationView.RenderDraft(draft, account.Profile, editable ? draftMessage : reason, editable, Busy || modal);
            }
            view.PresentPartyCharacters(party.Party);
        }
        private static bool AllReady(PartyData data)
        {
            if (data.members == null || data.members.Length == 0) return false;
            foreach (var member in data.members) if (member == null || !member.ready) return false;
            return true;
        }
        private void Message(string message)
        { modal = true; confirmed = null; view.messageDialog.Show(message); Refresh(); }
        private void Ask(string message, Action action)
        { modal = true; confirmed = action; view.confirmDialog.Show(message); Refresh(); }
        private void Confirm()
        { if (disposed) return; var action = confirmed; CloseDialog(); action?.Invoke(); }
        private void CloseDialog()
        { if (disposed) return; modal = false; confirmed = null; view.messageDialog.Hide(); view.confirmDialog.Hide(); Refresh(); }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; confirmed = null;
            foreach (var page in view.pages) page.ActionRequested -= Execute;
            if (view.Wardrobe != null) { view.Wardrobe.Changed -= Dress; view.Wardrobe.UndoRequested -= UndoDress; }
            view.roomsView.JoinRequested -= Join; view.customizationView.ChoiceRequested -= ChangeChoice;
            foreach (var page in view.pages) if (page is ProfilePanelView profile)
            { profile.AvatarRequested -= SaveAvatar; profile.PersonalRequested -= SavePersonal; }
            foreach (var page in view.pages) if (page is FPS.Settings.SettingsPanelView settings)
                settings.CloseRequested -= SettingsClosed;
            view.messageDialog.Confirmed -= CloseDialog; view.messageDialog.Cancelled -= CloseDialog;
            view.confirmDialog.Confirmed -= Confirm; view.confirmDialog.Cancelled -= CloseDialog;
            account.StateChanged -= AccountChanged; account.ProfileChanged -= ProfileChanged;
            party.Changed -= PartyChanged; party.BrowseRooms = false; lobby.OnLobbyChanged -= LobbyChanged;
            Model.Changed -= NavigationChanged; custom.Dispose();
        }
    }
}
