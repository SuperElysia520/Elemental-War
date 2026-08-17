using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 大厅状态（NetworkBehaviour）：房间内玩家列表与开局状态，由服务器权威。
/// 只存「谁在房间里」「是否开局」，玩家名用 FixedString64Bytes（NetworkList 要求 unmanaged 元素）。
/// </summary>
public class LobbyState : NetworkBehaviour
{
    /// <summary>房间内玩家名（按加入顺序，索引 0 = 房主）</summary>
    public NetworkList<FixedString64Bytes> PlayerNames = new NetworkList<FixedString64Bytes>();

    /// <summary>是否已开局（开局后拒绝新玩家加入）</summary>
    public NetworkVariable<bool> IsStarted = new NetworkVariable<bool>(false);

    /// <summary>房间名</summary>
    public NetworkVariable<FixedString64Bytes> RoomName = new NetworkVariable<FixedString64Bytes>();

    /// <summary>本地 UI 刷新回调（列表或状态变化时触发，在任意端）</summary>
    public event Action OnChanged;

    public int PlayerCount => PlayerNames.Count;

    public override void OnNetworkSpawn()
    {
        PlayerNames.OnListChanged += HandleListChanged;
        IsStarted.OnValueChanged += HandleBoolChanged;
        RoomName.OnValueChanged += HandleStringChanged;

        // 注册到 LobbyManager，供客机在同步完成后定位到本实例
        if (LobbyManager.Instance != null)
        {
            LobbyManager.Instance.RegisterLobby(this);
        }

        OnChanged?.Invoke();
    }

    public override void OnNetworkDespawn()
    {
        PlayerNames.OnListChanged -= HandleListChanged;
        IsStarted.OnValueChanged -= HandleBoolChanged;
        RoomName.OnValueChanged -= HandleStringChanged;
    }

    private void HandleListChanged(NetworkListEvent<FixedString64Bytes> changeEvent) => OnChanged?.Invoke();
    private void HandleBoolChanged(bool previousValue, bool newValue) => OnChanged?.Invoke();
    private void HandleStringChanged(FixedString64Bytes previousValue, FixedString64Bytes newValue) => OnChanged?.Invoke();

    // ---- 服务器端操作（默认写权限为 Server，只有房主/Host 能调用）----

    public void AddPlayer(string name)
    {
        if (!IsServer)
            return;
        PlayerNames.Add(new FixedString64Bytes(name));
    }

    public void RemovePlayer(string name)
    {
        if (!IsServer)
            return;
        PlayerNames.Remove(new FixedString64Bytes(name));
    }

    public void SetStarted(bool started)
    {
        if (!IsServer)
            return;
        IsStarted.Value = started;
    }

    public void SetRoomName(string name)
    {
        if (!IsServer)
            return;
        RoomName.Value = new FixedString64Bytes(name);
    }

    /// <summary>获取房间名（string 形式，供 UI 使用）</summary>
    public string GetRoomName()
    {
        return RoomName.Value.ToString();
    }

    /// <summary>获取房间内玩家名列表（string 形式，供 UI 使用）</summary>
    public List<string> GetPlayerNames()
    {
        var names = new List<string>(PlayerNames.Count);
        foreach (var n in PlayerNames)
        {
            names.Add(n.Value);
        }
        return names;
    }
}
