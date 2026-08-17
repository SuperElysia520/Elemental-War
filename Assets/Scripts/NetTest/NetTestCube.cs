using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 阶段0网络验证：Host/Server 端驱动方块移动，客户端观察同步
/// 验证 NGO 安装、连接、NetworkTransform 状态同步是否工作
/// </summary>
public class NetTestCube : NetworkBehaviour
{
    [Tooltip("移动半径")] public float radius = 5f;
    [Tooltip("移动速度")] public float speed = 2f;

    public override void OnNetworkSpawn()
    {
        // 只有服务器端驱动移动
        if (IsServer)
            StartCoroutine(MoveCube());
    }

    private IEnumerator MoveCube()
    {
        float angle = 0f;
        while (true)
        {
            angle += speed * Time.deltaTime;
            transform.position = new Vector3(Mathf.Cos(angle) * radius, 0.5f, Mathf.Sin(angle) * radius);
            yield return null;
        }
    }
}
