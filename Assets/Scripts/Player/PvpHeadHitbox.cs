using UnityEngine;

/// <summary>服务器端 PVP 头部命中区；位置始终跟随 Humanoid 头部骨骼。</summary>
[DisallowMultipleComponent]
public class PvpHeadHitbox : MonoBehaviour
{
    private Transform m_HeadBone;

    public PvpPlayerCombat Owner { get; private set; }

    public void Initialize(PvpPlayerCombat owner, Transform headBone, float radius, float verticalOffset)
    {
        Owner = owner;
        m_HeadBone = headBone;
        transform.SetParent(owner.transform, false);
        transform.localScale = Vector3.one;

        SphereCollider sphere = GetComponent<SphereCollider>();
        if (sphere == null)
            sphere = gameObject.AddComponent<SphereCollider>();
        sphere.isTrigger = true;
        sphere.radius = Mathf.Max(0.01f, radius);
        sphere.center = Vector3.zero;

        FollowHeadBone(verticalOffset);
        m_VerticalOffset = verticalOffset;
    }

    private float m_VerticalOffset;

    private void LateUpdate()
    {
        FollowHeadBone(m_VerticalOffset);
    }

    private void FollowHeadBone(float verticalOffset)
    {
        if (m_HeadBone == null)
            return;

        transform.position = m_HeadBone.position + Vector3.up * verticalOffset;
        transform.rotation = m_HeadBone.rotation;
    }
}
