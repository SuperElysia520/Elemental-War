using System;
using UnityEngine;

/// <summary>Mechanical geometry and authored contact points. Contains no character skeleton.</summary>
public sealed class PvpWeaponModel : MonoBehaviour
{
    public Transform muzzle;
    public Transform ejectionPort;
    public Transform rightGrip;
    public Transform leftGrip;
    public Transform magazine;
    public Transform reloadHandTarget;
    public Transform bolt;
    public PvpSightModel[] sights;
    public ParticleSystem[] muzzleEffects;
    public Animation mechanicalAnimation;
    public AnimationClip fireAnimation;
    public AnimationClip reloadAnimation;
    public AnimationClip equipAnimation;
    public AnimationClip idleAnimation;
    public GameObject casingPrefab;
    public Vector3 neutralPosition;
    public Vector3 neutralEuler;

    public Transform SelectSight(byte id)
    {
        Transform result = null;
        if (sights == null) return result;
        foreach (var entry in sights)
        {
            bool selected = entry.sightId == id;
            if (entry.visual != null) entry.visual.SetActive(selected);
            if (selected) result = entry.aimPoint;
        }
        if (result == null && sights.Length > 0)
        {
            if (sights[0].visual != null) sights[0].visual.SetActive(true);
            result = sights[0].aimPoint;
        }
        return result;
    }
}

[Serializable]
public sealed class PvpSightModel
{
    public byte sightId;
    public GameObject visual;
    public Transform aimPoint;
    [Min(0.12f)] public float eyeRelief = 0.12f;
    // Model-local vertices on a near-eye receiver edge, when the sight has no separate mesh.
    public Vector3[] nearEyePoints = Array.Empty<Vector3>();
}
