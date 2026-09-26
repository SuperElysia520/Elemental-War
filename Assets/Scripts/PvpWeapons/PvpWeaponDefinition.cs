using System;
using UnityEngine;

[CreateAssetMenu(menuName = "PVP/Weapon Definition")]
public sealed class PvpWeaponDefinition : ScriptableObject
{
    public byte weaponId;
    public string displayName;
    public GameObject modelPrefab;
    public bool automatic;
    public bool selectiveFire;
    public float roundsPerMinute = 800f;
    [Tooltip("The Demo sniper's Fire state blocks another shot while its bolt cycle plays.")]
    public float actionCycleDuration;
    public int magazineCapacity = 30;
    public int reserveAmmo = 200;
    public bool keepChamberedRound = true;
    public float reloadDuration = 1.925f;
    public float emptyReloadDuration = 1.925f;
    public float equipDuration = 0.35f;
    public float muzzleVelocity = 550f;
    public float gravity = 9.81f;
    public float drag = 0.1f;
    public float projectileLifetime = 5f;
    public float hitRadius = 0.01f;
    public float damage = 25f;
    public float headDamageMultiplier = 2.5f;
    public float limbDamageMultiplier = 1f;
    public float range = 300f;
    public float impactForce = 10f;
    public AnimationCurve damageOverRange = AnimationCurve.Linear(0f, 1f, 1f, 0f);
    public float aimDuration = 0.2f;
    public float aimMovementMultiplier = 0.5f;
    public float fireMovementMultiplier = 0.4f;
    public PvpSightDefinition[] sights;
    public PvpSprayDefinition hipSpray;
    public PvpSprayDefinition aimSpray;
    public PvpKickDefinition hipKick;
    public PvpKickDefinition aimKick;
    public PvpMotionDefinition motion = new PvpMotionDefinition();
    public float cameraRecoil = 2f;
    public float horizontalRecoil = 1f;
    public float verticalRecoil = 0.3f;
    public float cameraShakeAmount = 0.1f;
    public float cameraShakeRoughness = 1f;
    public float cameraShakeDuration = 0.3f;
    public AudioClip fireClip;
    public AudioClip reloadClip;
    public AudioClip emptyReloadClip;
    public AudioClip equipClip;
    public float firePitch = 1f;
    public float firePitchRandomness;
    public float fireVolume = 1f;
    public float reloadPitch = 1f;
    public float audioMaxDistance = 200f;
    public PvpAudioCue[] fireCues;
    public PvpAudioCue[] reloadCues;
    public PvpAudioCue[] emptyReloadCues;
    public PvpAudioCue[] equipCues;

    public float ShotInterval => Mathf.Max(60f / Mathf.Max(1f, roundsPerMinute), actionCycleDuration);

    public PvpSightDefinition GetSight(byte id)
    {
        if (sights == null || sights.Length == 0) return null;
        foreach (var sight in sights) if (sight != null && sight.sightId == id) return sight;
        return sights[0];
    }

    public void ApplyToLegacy(PlayerWeapon target)
    {
        if (target == null) return;
        target.damage = damage;
        target.range = range;
        target.bulletInterval = ShotInterval;
        target.magazineSize = magazineCapacity;
        target.fallbackReloadDuration = reloadDuration;
        target.singleShotSound = fireClip;
        target.emptyReloadSound = emptyReloadClip;
        target.partialReloadSound = reloadClip;
    }
}

[Serializable]
public sealed class PvpAudioCue
{
    public float time;
    public AudioClip clip;
}

[Serializable]
public sealed class PvpSightDefinition
{
    public byte sightId;
    public string displayName;
    public float fieldOfView = 55f;
    [Tooltip("Reference-only projection value; the full body uses the world camera.")]
    public float referenceWeaponFieldOfView = 50f;
    public float secondaryFieldOfView;
    public bool magnified;
}

[Serializable]
public sealed class PvpSprayDefinition
{
    public float maxAmount = 6f;
    public float passiveMultiplier = 0.3f;
    public float rampUpTime = 0.5f;
    public float recoveryTime = 0.1f;
    public bool autoFill = true;
    public bool loop = true;
    [Tooltip("x = right/left, y = up/down, copied from the Demo's fixed pattern.")]
    public Vector2[] points = { Vector2.zero };
}

[Serializable]
public sealed class PvpKickDefinition
{
    public Vector3 staticPosition;
    public Vector3 staticRotation;
    public Vector3 randomPosition;
    public Vector3 randomRotation;
    public float positionRoughness = 10f;
    public float rotationRoughness = 10f;
    public Vector3 springPosition;
    public Vector3 springRotation;
    public float springPositionSpeed = 10f;
    public float springRotationSpeed = 10f;
    public float springPositionFade = 1f;
    public float springRotationFade = 1f;
    public float springSpeedMultiplier = 1f;
}

[Serializable]
public sealed class PvpMotionDefinition
{
    public float swayAmplification = 0.5f;
    public float swaySmoothness = 5f;
    public float swayPositionInput = 0.0005f;
    public float swayRotationInput = 0.2f;
    public float swayPositionLimit = 1f;
    public float swayRotationLimit = 20f;
    public Vector3 hipBreathPosition = new Vector3(0f, 0.005f, 0f);
    public Vector3 hipBreathRotation = new Vector3(-0.68f, 0f, 1f);
    public Vector3 aimBreathPosition = new Vector3(0.0005f, 0.001f, 0f);
    public Vector3 aimBreathPositionFrequency = new Vector3(1.21f, 1f, 1f);
    public Vector3 aimBreathRotation = new Vector3(-0.1f, 0f, 0f);
    public Vector3 walkPosition = new Vector3(0.00065f, 0.00065f, 0.00065f);
    public Vector3 walkPositionFrequency = new Vector3(1f, 2f, 2f);
    public Vector3 walkRotation = new Vector3(0.0455f, 0f, -0.39f);
    public Vector3 walkRotationFrequency = new Vector3(2f, 1f, 1f);
    public Vector3 sprintPosition = new Vector3(-0.05f, -0.06f, -0.05f);
    public Vector3 sprintRotation = new Vector3(11.1f, -16.49f, 28.2f);
    public float sprintDuration = 0.25f;
}
