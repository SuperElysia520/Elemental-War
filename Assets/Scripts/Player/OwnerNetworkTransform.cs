using Unity.Netcode.Components;

/// <summary>
/// Owner 权威的 NetworkTransform：由持有该玩家的客户端写位置/旋转，同步给其他客户端与服务器。
/// NGO 1.15.1 没有 AuthorityMode 字段，通过 override OnIsServerAuthoritative() 返回 false 实现。
/// </summary>
public class OwnerNetworkTransform : NetworkTransform
{
    protected override bool OnIsServerAuthoritative()
    {
        return false;
    }
}
