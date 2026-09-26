using UnityEngine;

public enum PvpImpactSurface : byte { Stone, Metal, Wood, Character }

/// <summary>Scene-owned, bounded pool for the Demo's decals and impact particles.</summary>
public sealed class PvpImpactEffects : MonoBehaviour
{
    private sealed class Entry
    {
        public GameObject instance;
        public ParticleSystem[] particles;
        public PvpImpactSurface surface;
        public float expires;
        public Transform follow;
        public Vector3 localPoint;
        public Quaternion localRotation;
    }

    private const int Capacity = 48;
    private static PvpImpactEffects instance;
    private readonly Entry[] entries = new Entry[Capacity];
    private readonly GameObject[] prefabs = new GameObject[4];
    private int next;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => instance = null;

    public static void Play(Vector3 position, Vector3 normal, bool character, Transform follow = null)
        => Play(position, normal, character ? PvpImpactSurface.Character : PvpImpactSurface.Stone, follow);

    public static void Play(Vector3 position, Vector3 normal, PvpImpactSurface surface, Transform follow = null)
    {
        if (Application.isBatchMode) return;
        if (instance == null) instance = new GameObject("PVP Impact Effects").AddComponent<PvpImpactEffects>();
        instance.Emit(position, normal, surface, follow);
    }

    private void Emit(Vector3 position, Vector3 normal, PvpImpactSurface surface, Transform follow)
    {
        int surfaceIndex = Mathf.Clamp((int)surface, 0, 3);
        if (prefabs[surfaceIndex] == null)
        {
            string name = surfaceIndex == 3 ? "Blood" : ((PvpImpactSurface)surfaceIndex).ToString();
            prefabs[surfaceIndex] = Resources.Load<GameObject>("PvpWeapons/Effects/" + name + "Impact");
        }
        if (prefabs[surfaceIndex] == null) return;
        int index = next++ % Capacity;
        var entry = entries[index];
        if (entry == null || entry.surface != surface)
        {
            if (entry != null && entry.instance != null) Destroy(entry.instance);
            var effect = Instantiate(prefabs[surfaceIndex], transform);
            entry = new Entry { instance = effect, particles = effect.GetComponentsInChildren<ParticleSystem>(true), surface = surface };
            entries[index] = entry;
        }
        Quaternion rotation = Quaternion.LookRotation(normal.sqrMagnitude > 0.001f ? normal.normalized : Vector3.up);
        Vector3 hitPosition = position + normal.normalized * 0.002f;
        entry.instance.transform.SetPositionAndRotation(hitPosition, rotation);
        entry.follow = follow;
        entry.localPoint = follow != null ? follow.InverseTransformPoint(hitPosition) : Vector3.zero;
        entry.localRotation = follow != null ? Quaternion.Inverse(follow.rotation) * rotation : Quaternion.identity;
        entry.expires = Time.time + 5f;
        entry.instance.SetActive(true);
        foreach (var particles in entry.particles)
        {
            particles.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            particles.Play(false);
        }
    }

    private void LateUpdate()
    {
        for (int i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry == null || entry.instance == null || !entry.instance.activeSelf) continue;
            if (Time.time >= entry.expires) { entry.instance.SetActive(false); continue; }
            if (entry.follow != null)
                entry.instance.transform.SetPositionAndRotation(entry.follow.TransformPoint(entry.localPoint), entry.follow.rotation * entry.localRotation);
        }
    }

    private void OnDestroy() { if (instance == this) instance = null; }
}
