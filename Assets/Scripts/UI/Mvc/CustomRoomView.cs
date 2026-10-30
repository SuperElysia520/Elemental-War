using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace FPS.UI
{
    [Serializable] public sealed class CustomTeamSlot
    {
        public UnityEngine.UI.Button button;
        public UnityEngine.UI.Image background;
        public UnityEngine.UI.Text label;
        public LobbyTeam team;
    }
    public sealed class CustomRoomView : LobbyPageView
    {
        public GameObject browsePanel, roomPanel, teamPanel;
        public TMP_InputField roomName, localName;
        public Transform content;
        public GameObject rowPrefab;
        public UnityEngine.UI.Button joinButton, createButton, startButton;
        public UnityEngine.UI.Text empty, roomTitle, memberList, roomStatus, redHeader, blueHeader, hint;
        public TMP_Text browseTitle;
        public CustomTeamSlot[] teamSlots = new CustomTeamSlot[0];
        private readonly List<GameObject> rows = new List<GameObject>();
        public event Action<LanDiscovery.RoomEntry> RoomSelected;
        public event Action<LanDiscovery.RoomEntry> JoinRequested;
        public void RenderRooms(IReadOnlyList<LanDiscovery.RoomEntry> rooms, LanDiscovery.RoomEntry selected, LobbyGameMode mode, bool busy)
        {
            foreach (var row in rows) Destroy(row);
            rows.Clear();
            foreach (var room in rooms)
            {
                if (room.gameMode != mode || mode == LobbyGameMode.PVP && (!room.isDedicatedPvp || !room.hasRoom)) continue;
                var row = Instantiate(rowPrefab, content); rows.Add(row);
                string text = room.roomName + "  [" + (room.isStarted ? "对局中" : room.currentPlayers + "/" + room.maxPlayers + "人") + "]";
                var label = row.GetComponentInChildren<TMP_Text>();
                if (label != null) { label.text = text; label.color = SameRoom(room, selected) ? Color.green : Color.white; }
                else { var legacy = row.GetComponentInChildren<UnityEngine.UI.Text>(); if (legacy != null) legacy.text = text; }
                var button = row.GetComponent<UnityEngine.UI.Button>();
                if (button != null) { var captured = room; button.interactable = !busy; button.onClick.AddListener(() => RoomSelected?.Invoke(captured)); }
                var card = row.GetComponent<RoomEntryUI>();
                if (card != null && card.joinButton != null)
                {
                    var captured = room; card.joinButton.interactable = !busy && !room.isStarted && room.currentPlayers < room.maxPlayers;
                    card.joinButton.onClick.AddListener(() => JoinRequested?.Invoke(captured));
                }
            }
            if (empty != null) { empty.gameObject.SetActive(rows.Count == 0); empty.text = "（暂无已创建的房间）"; }
            if (joinButton != null) joinButton.interactable = !busy && selected != null && !selected.isStarted && selected.currentPlayers < selected.maxPlayers;
        }
        public void RenderRoom(bool inRoom, bool pvp, string title, string message, IList<LobbyPlayerData> red, IList<LobbyPlayerData> blue,
            IList<string> pveNames, ulong localId, ulong ownerId, bool canStart, bool locked)
        {
            browsePanel.SetActive(!inRoom); roomPanel.SetActive(inRoom); teamPanel.SetActive(inRoom && pvp);
            if (memberList != null) { memberList.gameObject.SetActive(!pvp); memberList.text = pveNames != null ? string.Join("\n", pveNames) : ""; }
            if (roomTitle != null) roomTitle.text = title;
            if (roomStatus != null) roomStatus.text = message;
            if (hint != null) hint.text = message;
            if (startButton != null) startButton.interactable = canStart;
            if (redHeader != null) redHeader.text = "红队 " + (red != null ? red.Count : 0) + "/5";
            if (blueHeader != null) blueHeader.text = "蓝队 " + (blue != null ? blue.Count : 0) + "/5";
            int redIndex = 0, blueIndex = 0;
            foreach (var slot in teamSlots)
            {
                var players = slot.team == LobbyTeam.Red ? red : blue;
                int index = slot.team == LobbyTeam.Red ? redIndex++ : blueIndex++;
                bool occupied = players != null && index < players.Count;
                slot.label.text = occupied ? (index + 1) + ". " + players[index].PlayerName + (players[index].ClientId == localId ? "（我）" : "")
                    + (players[index].ClientId == ownerId ? "（房主）" : "") : (index + 1) + ". 空位（点击加入）";
                slot.button.interactable = !locked && !occupied && inRoom;
                slot.background.color = slot.team == LobbyTeam.Red ? new Color(0.55f, 0.12f, 0.12f, occupied ? 0.82f : 0.58f)
                    : new Color(0.10f, 0.25f, 0.58f, occupied ? 0.82f : 0.58f);
            }
        }
        public static bool SameRoom(LanDiscovery.RoomEntry a, LanDiscovery.RoomEntry b) => a != null && b != null
            && Equals(a.hostEndPoint, b.hostEndPoint) && a.gamePort == b.gamePort;
    }
}
