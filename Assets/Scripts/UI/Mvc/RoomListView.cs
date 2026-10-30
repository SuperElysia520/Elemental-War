using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace FPS.UI
{
    public sealed class RoomListView : LobbyPageView
    {
        public Transform content;
        public RoomEntryUI entryPrefab;
        public TMP_Text empty;
        private readonly Dictionary<string, RoomEntryUI> rows = new Dictionary<string, RoomEntryUI>();
        public event Action<string> JoinRequested;
        public void RenderRooms(PartyData[] rooms, PlayerAvatarCatalog catalog, bool canJoin)
        {
            var alive = new HashSet<string>();
            foreach (var room in rooms)
            {
                if (room == null || string.IsNullOrEmpty(room.id) || !alive.Add(room.id)) continue;
                if (!rows.TryGetValue(room.id, out var row))
                { row = Instantiate(entryPrefab, content); rows.Add(room.id, row); }
                string id = room.id;
                row.Show(room, catalog, canJoin, () => JoinRequested?.Invoke(id));
                row.transform.SetSiblingIndex(alive.Count - 1);
            }
            var removed = new List<string>();
            foreach (var row in rows)
                if (!alive.Contains(row.Key)) { Destroy(row.Value.gameObject); removed.Add(row.Key); }
            foreach (string id in removed) rows.Remove(id);
            if (empty != null) { empty.gameObject.SetActive(alive.Count == 0); empty.text = "暂无已创建的队伍"; }
        }
    }
}
