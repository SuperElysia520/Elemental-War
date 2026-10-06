using UnityEngine;

/// <summary>Stable preset indices must have identical ordering in every client build.</summary>
[CreateAssetMenu(menuName = "FPS/玩家预设头像", fileName = "PlayerAvatars")]
public sealed class PlayerAvatarCatalog : ScriptableObject
{
    [Tooltip("最多 32 个预设头像；数组索引作为账号保存的头像 ID。")]
    public Sprite[] avatars = new Sprite[0];
    public Sprite Get(byte id) => avatars != null && id < avatars.Length ? avatars[id] : null;
}
