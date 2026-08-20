using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 联机模式下隐藏并停用自身（用于 Game 场景里固定摆放的单机角色实例）。
/// 联机时由网络生成的角色取代它们；单机（无网络会话）时保持原样，不影响现有单机路径。
/// 注意：还要调用 PlayerModel.Stop() + enabled=false，因为角色的状态机通过 MonoManager 委托驱动，
/// 单纯 SetActive(false) 不会停掉它，会继续跑 AI 跟随。
/// </summary>
public class HideInMultiplayer : MonoBehaviour
{
    void Start()
    {
        // 联网玩家（带 NetworkObject，由房主动态 Spawn）不受本组件影响，只隐藏场景里固定摆放的单机角色
        if (GetComponent<NetworkObject>() != null)
            return;

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
        {
            var playerModel = GetComponent<PlayerModel>();
            if (playerModel != null)
            {
                playerModel.Stop();
                playerModel.enabled = false;
            }

            gameObject.SetActive(false);
        }
    }
}
