using UnityEngine;
using UnityEngine.UI;

public sealed class PlayerAvatarView : MonoBehaviour
{
    public Image image;
    public RawImage rawImage;
    public void Show(byte id, PlayerAvatarCatalog catalog)
    {
        Sprite sprite = catalog != null ? catalog.Get(id) : null;
        if (image != null) { image.sprite = sprite; image.enabled = sprite != null; }
        if (rawImage != null)
        {
            rawImage.texture = sprite != null ? sprite.texture : null;
            rawImage.enabled = sprite != null;
            if (sprite != null)
            {
                Rect rect = sprite.textureRect;
                rawImage.uvRect = new Rect(rect.x / sprite.texture.width, rect.y / sprite.texture.height,
                    rect.width / sprite.texture.width, rect.height / sprite.texture.height);
            }
        }
    }
}
