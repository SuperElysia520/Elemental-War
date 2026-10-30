using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace FPS.UI
{
    /// <summary>Custom-match and LAN operations, separate from prefab rendering and five-player parties.</summary>
    public sealed class CustomRoomController : IDisposable
    {
        private readonly LobbyUIRoot host;
        private readonly LobbyManager lobby;
        private readonly LobbyModel model;
        private readonly Action refresh;
        private readonly LanDiscovery discovery;
        private Coroutine creating;
        private bool visible, disposed;
        private CustomRoomView View => host.customRoomView;
        public CustomRoomController(LobbyUIRoot host, LobbyManager lobby, LobbyModel model, Action refresh)
        { this.host = host; this.lobby = lobby; this.model = model; this.refresh = refresh; discovery = lobby.discovery; }
        public void Initialize()
        {
            View.RoomSelected += Select;
            View.JoinRequested += Join;
            if (discovery != null) discovery.OnRoomsChanged += RoomsChanged;
        }
        public void Open(LobbyGameMode mode)
        {
            model.CustomMode = lobby.CurrentLobby != null ? lobby.CurrentLobby.GameMode : mode;
            model.SelectedRoom = null;
            if (View.localName != null) View.localName.text = lobby.PlayerName;
            if (View.browseTitle != null) View.browseTitle.text = model.CustomMode == LobbyGameMode.PVP ? "创建PVP联机房间" : "创建PVE联机房间";
            lobby.SetBrowsingGameMode(model.CustomMode);
        }
        public void SetVisible(bool value)
        {
            if (visible == value) return;
            visible = value;
            if (value) { if (!lobby.IsInLobby) RefreshRooms(); }
            else StopDiscovery();
        }
        private void Select(LanDiscovery.RoomEntry room) { if (!visible || disposed) return; model.SelectedRoom = room; Render(); }
        private void Join(LanDiscovery.RoomEntry room)
        { if (!visible || disposed) return; model.SelectedRoom = room; host.Controller.Execute(LobbyAction.JoinRoom); }
        private void RoomsChanged()
        {
            if (!visible || disposed) return;
            if (model.SelectedRoom != null)
            {
                var selected = model.SelectedRoom; model.SelectedRoom = null;
                foreach (var room in discovery.Rooms) if (CustomRoomView.SameRoom(room, selected)) { model.SelectedRoom = room; break; }
            }
            Render();
        }
        public void Execute(LobbyAction action)
        {
            if (!visible || disposed || model.IsBusy) return;
            switch (action)
            {
                case LobbyAction.RefreshCustomRooms: RefreshRooms(); break;
                case LobbyAction.CreateRoom:
                    if (lobby.IsInLobby || lobby.IsConnecting) return;
                    ApplyName();
                    if (model.CustomMode == LobbyGameMode.PVE) lobby.CreateRoom(RoomName(), LobbyGameMode.PVE);
                    else creating = host.StartCoroutine(CreatePvp());
                    break;
                case LobbyAction.JoinRoom:
                    if (model.SelectedRoom == null || lobby.IsInLobby || lobby.IsConnecting) return;
                    ApplyName();
                    if (model.CustomMode == LobbyGameMode.PVP)
                    { lobby.TryJoinDedicatedPvpRoom(model.SelectedRoom, out string message); model.Status = message; }
                    else lobby.JoinRoom(model.SelectedRoom);
                    break;
                case LobbyAction.StartRoom: lobby.TryStartGame(out string status); model.Status = status; break;
                case LobbyAction.LeaveRoom: lobby.LeaveRoom(); RefreshRooms(); break;
                case LobbyAction.SelectRed: SelectTeam(LobbyTeam.Red); break;
                case LobbyAction.SelectBlue: SelectTeam(LobbyTeam.Blue); break;
                case LobbyAction.SelectCharacter: lobby.SelectCharacter(0); break;
            }
            refresh();
        }
        private void SelectTeam(LobbyTeam team)
        {
            var state = lobby.CurrentLobby;
            if (state != null && !state.IsStarted.Value && !state.PvpIsPreparingMatch.Value && state.GetTeamCount(team) < LobbyState.PvpTeamCapacity)
                lobby.SelectTeam(team);
        }
        private IEnumerator CreatePvp()
        {
            model.IsBusy = true;
            lobby.StartDedicatedPvpRoomDiscovery(out string message); model.Status = message;
            lobby.RefreshDedicatedPvpRoomDiscovery(); refresh();
            float deadline = Time.unscaledTime + 1.5f;
            bool submitted = false;
            while (Time.unscaledTime < deadline && !disposed)
            {
                if (discovery != null && discovery.TryGetIdleDedicatedPvpServer(out var server))
                { submitted = lobby.TryCreateDedicatedPvpRoom(RoomName(), server, out message); model.Status = message; break; }
                yield return null;
            }
            if (disposed) yield break;
            if (!submitted && string.IsNullOrEmpty(model.Status)) model.Status = "暂未找到可用的 PVP 服务器，请刷新后重试。";
            else if (!submitted && Time.unscaledTime >= deadline) model.Status = "暂未找到可用的 PVP 服务器，请刷新后重试。";
            model.IsBusy = false; creating = null; refresh();
        }
        private void RefreshRooms()
        {
            model.SelectedRoom = null;
            if (lobby.IsInLobby || lobby.IsConnecting) return;
            if (model.CustomMode == LobbyGameMode.PVP)
            { lobby.StartDedicatedPvpRoomDiscovery(out string message); model.Status = message; lobby.RefreshDedicatedPvpRoomDiscovery(); }
            else if (discovery != null) { discovery.ClearRooms(); discovery.StartListening(); }
            Render();
        }
        public void Render()
        {
            if (disposed || !visible) return;
            var state = lobby.CurrentLobby;
            bool pvp = state != null ? state.GameMode == LobbyGameMode.PVP : model.CustomMode == LobbyGameMode.PVP;
            bool locked = model.IsBusy || lobby.IsConnecting || state != null && (state.IsStarted.Value || state.PvpIsPreparingMatch.Value);
            if (View.createButton != null) View.createButton.interactable = !locked && !lobby.IsInLobby;
            if (discovery != null && !lobby.IsInLobby) View.RenderRooms(discovery.Rooms, model.SelectedRoom, model.CustomMode, locked);
            bool canStart = !locked && lobby.CanManageRoom && state != null && !lobby.IsLocalPvpProfileSyncInProgress;
            if (canStart && pvp)
            {
                canStart = state.HasMinimumPvpTeams();
                foreach (var player in state.GetPlayers())
                    if (player.Team == LobbyTeam.None || player.CharacterIndex >= LobbyState.PvpCharacterCount) canStart = false;
            }
            string message = !string.IsNullOrEmpty(model.Status) ? model.Status : lobby.ConnectionStatus;
            View.RenderRoom(lobby.IsInLobby || lobby.IsConnecting, pvp, state != null ? state.RoomName.Value.ToString() : "正在连接房间…", message,
                state != null && pvp ? state.GetTeamPlayers(LobbyTeam.Red) : null, state != null && pvp ? state.GetTeamPlayers(LobbyTeam.Blue) : null,
                state != null && !pvp ? state.GetPlayerNames() : null, NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : ulong.MaxValue,
                state != null ? state.RoomOwnerClientId.Value : ulong.MaxValue, canStart, locked);
        }
        private string RoomName() => View.roomName != null && !string.IsNullOrWhiteSpace(View.roomName.text) ? View.roomName.text.Trim() : model.CustomMode == LobbyGameMode.PVP ? "PVP房间" : "PVE房间";
        private void ApplyName() { if (View.localName != null) lobby.PlayerName = View.localName.text; }
        private void StopDiscovery()
        {
            if (creating != null) { host.StopCoroutine(creating); creating = null; model.IsBusy = false; }
            if (lobby != null) lobby.StopDedicatedPvpRoomDiscovery();
            if (discovery != null) discovery.StopListening();
        }
        public void Close() { lobby.LeaveRoom(); SetVisible(false); model.SelectedRoom = null; }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            View.RoomSelected -= Select;
            View.JoinRequested -= Join;
            if (discovery != null) discovery.OnRoomsChanged -= RoomsChanged;
            StopDiscovery();
        }
    }
}
