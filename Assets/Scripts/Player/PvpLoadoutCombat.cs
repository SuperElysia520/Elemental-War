using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>Server owns ammunition, reload completion, slot changes and flight-time hits. Owner predicts only presentation.</summary>
[RequireComponent(typeof(PvpWeaponRig), typeof(PvpCharacterProfile))]
public sealed class PvpLoadoutCombat : NetworkBehaviour
{
    private readonly NetworkVariable<byte> slot = new NetworkVariable<byte>();
    private readonly NetworkVariable<int> primaryAmmo = new NetworkVariable<int>();
    private readonly NetworkVariable<int> secondaryAmmo = new NetworkVariable<int>();
    private readonly NetworkVariable<int> primaryReserve = new NetworkVariable<int>();
    private readonly NetworkVariable<int> secondaryReserve = new NetworkVariable<int>();
    private readonly NetworkVariable<double> reloadAt = new NetworkVariable<double>();
    private readonly NetworkVariable<double> equipAt = new NetworkVariable<double>();
    private readonly NetworkVariable<bool> automatic = new NetworkVariable<bool>(true);
    private readonly NetworkVariable<int> acknowledgedSequence = new NetworkVariable<int>();
    private readonly NetworkVariable<bool> triggerHeld = new NetworkVariable<bool>(writePerm: NetworkVariableWritePermission.Owner);
    private PvpWeaponRig rig;
    private PvpFullBodyController body;
    private PvpPlayerCombat health;
    private PvpCharacterProfile profile;
    private PlayerWeapon legacyWeapon;
    private double nextServerShot;
    private double presentationReloadEnd;
    private float hipMagnitude, aimMagnitude;
    private Vector3 previousPosition;
    private float nextLocalShot;
    private int sequence, serverSequence, burstIndex;
    private readonly Queue<int> predictedShots = new Queue<int>();
    private readonly List<Flight> flights = new List<Flight>();
    private readonly RaycastHit[] hits = new RaycastHit[64];
    private readonly Collider[] overlaps = new Collider[32];
    private readonly List<VisualFlight> visuals = new List<VisualFlight>();
    private static Material tracerMaterial;
    private struct Flight { public Vector3 origin, direction, position; public float age, distance; public PvpWeaponDefinition definition; }
    private struct VisualFlight { public Vector3 origin, direction; public float age; public PvpWeaponDefinition definition; public LineRenderer line; }
    public byte ActiveSlot => slot.Value;
    public int CurrentAmmo
    {
        get
        {
            while (predictedShots.Count > 0 && predictedShots.Peek() <= acknowledgedSequence.Value) predictedShots.Dequeue();
            return Mathf.Max(0, (slot.Value == 0 ? primaryAmmo.Value : secondaryAmmo.Value) - (IsOwner ? predictedShots.Count : 0));
        }
    }
    public int ReserveAmmo => slot.Value == 0 ? primaryReserve.Value : secondaryReserve.Value;
    private double Now => NetworkManager != null ? NetworkManager.ServerTime.Time : Time.timeAsDouble;
    public bool IsReloading => reloadAt.Value > Now;
    public bool IsSwitching => equipAt.Value > Now;
    public PvpWeaponDefinition ActiveDefinition => PvpWeaponCatalog.Get(slot.Value == 0 ? profile.Loadout.Value.primaryWeaponId : (byte)1);

    private void Awake()
    {
        rig = GetComponent<PvpWeaponRig>(); body = GetComponent<PvpFullBodyController>();
        profile = GetComponent<PvpCharacterProfile>(); health = GetComponent<PvpPlayerCombat>();
        legacyWeapon = GetComponentInChildren<PlayerWeapon>(true);
    }
    public override void OnNetworkSpawn()
    {
        slot.OnValueChanged += SlotChanged;
        if (health != null) health.IsDead.OnValueChanged += DeadChanged;
        if (IsServer) Refill();
        previousPosition = transform.position;
        ApplySlot();
        if (!IsServer && IsReloading)
        {
            var definition = ActiveDefinition;
            bool empty = CurrentAmmo == 0;
            BeginReloadPresentation(slot.Value, reloadAt.Value,
                empty ? definition.emptyReloadDuration : definition.reloadDuration, empty);
        }
    }
    public override void OnNetworkDespawn()
    {
        slot.OnValueChanged -= SlotChanged;
        if (health != null) health.IsDead.OnValueChanged -= DeadChanged;
        foreach (var visual in visuals) if (visual.line != null) Destroy(visual.line.gameObject);
        visuals.Clear(); flights.Clear();
        presentationReloadEnd = 0;
    }
    private void DeadChanged(bool before, bool dead)
    {
        rig.SetVisible(!dead);
        predictedShots.Clear();
        presentationReloadEnd = 0;
        rig.CancelReload();
        body.CancelActionAnimation();
        if (IsServer) { reloadAt.Value = 0; equipAt.Value = 0; if (!dead) Refill(); }
        if (!dead) GetComponent<PvpCharacterProfile>().ApplyAppearance();
    }
    private void Refill()
    {
        var primary = PvpWeaponCatalog.Get(profile.Loadout.Value.primaryWeaponId);
        var pistol = PvpWeaponCatalog.Get(1);
        if (primary == null || pistol == null) { Debug.LogError("[PVP] 武器资源未生成。"); return; }
        primaryAmmo.Value = primary.magazineCapacity; secondaryAmmo.Value = pistol.magazineCapacity;
        primaryReserve.Value = primary.reserveAmmo; secondaryReserve.Value = pistol.reserveAmmo;
        slot.Value = 0; reloadAt.Value = 0; equipAt.Value = Now + primary.equipDuration;
        automatic.Value = primary.automatic; nextServerShot = Now; ResetSpray(primary);
    }
    public void CancelLocalInput() { if (IsOwner && IsSpawned) triggerHeld.Value = false; }
    public void SetTrigger(bool held, bool pressed, bool aiming, Vector3 direction)
    {
        if (IsOwner && IsSpawned) triggerHeld.Value = held && CanFight() && !IsReloading && !IsSwitching;
        var definition = ActiveDefinition;
        if (!IsOwner || !held || definition == null || IsReloading || IsSwitching || !CanFight()) return;
        bool auto = definition.selectiveFire ? automatic.Value : definition.automatic;
        if (!auto && !pressed) return;
        if (CurrentAmmo <= 0) { if (ReserveAmmo > 0) RequestReload(); return; }
        if (Time.time < nextLocalShot) return;
        FinishElapsedReloadPresentation();
        nextLocalShot = Time.time + definition.ShotInterval;
        predictedShots.Enqueue(++sequence);
        rig.PlayShot(true);
        body.AddShotRecoil(definition);
        FireServerRpc(sequence, direction, aiming);
    }
    public void RequestReload() { if (IsOwner && CanFight() && !IsReloading && !IsSwitching) ReloadServerRpc(); }
    public void RequestSlot(byte value) { if (IsOwner && value <= 1 && value != slot.Value && CanFight()) SwitchServerRpc(value); }
    public void RequestFireMode() { if (IsOwner && ActiveDefinition != null && ActiveDefinition.selectiveFire) FireModeServerRpc(); }

    [ServerRpc] private void FireModeServerRpc() { if (CanFight() && !IsReloading && ActiveDefinition.selectiveFire) automatic.Value = !automatic.Value; }
    [ServerRpc] private void SwitchServerRpc(byte value)
    {
        if (value > 1 || value == slot.Value || !CanFight() || IsSwitching) return;
        reloadAt.Value = 0; slot.Value = value;
        var definition = ActiveDefinition;
        equipAt.Value = Now + definition.equipDuration; nextServerShot = equipAt.Value;
        automatic.Value = definition.automatic; ResetSpray(definition);
    }
    private void SlotChanged(byte before, byte after)
    {
        predictedShots.Clear(); presentationReloadEnd = 0;
        rig.CancelReload(); body.CancelActionAnimation(); ApplySlot();
    }
    private void ApplySlot()
    {
        rig.SelectSlot(slot.Value);
        var definition = ActiveDefinition;
        if (definition != null) definition.ApplyToLegacy(legacyWeapon);
        if (legacyWeapon != null) legacyWeapon.bulletSpawnPoint = rig.Muzzle;
        if (definition != null) body.PlayEquipAnimation(definition.equipDuration);
    }
    [ServerRpc] private void ReloadServerRpc()
    {
        var definition = ActiveDefinition;
        if (definition == null || !CanFight() || IsReloading || IsSwitching) return;
        int ammo = slot.Value == 0 ? primaryAmmo.Value : secondaryAmmo.Value;
        int capacity = definition.magazineCapacity + (definition.keepChamberedRound && ammo > 0 ? 1 : 0);
        if (ammo >= capacity || ReserveAmmo <= 0) return;
        float duration = ammo == 0 ? definition.emptyReloadDuration : definition.reloadDuration;
        reloadAt.Value = Now + duration;
        ReloadClientRpc(slot.Value, reloadAt.Value, duration, ammo == 0);
    }
    [ClientRpc] private void ReloadClientRpc(byte reloadSlot, double completedAt, float duration, bool empty)
    {
        BeginReloadPresentation(reloadSlot, completedAt, duration, empty);
    }
    private void BeginReloadPresentation(byte reloadSlot, double completedAt, float duration, bool empty)
    {
        if (reloadSlot != slot.Value || health == null || health.IsDead.Value || completedAt <= Now) return;
        presentationReloadEnd = completedAt;
        float remaining = Mathf.Clamp((float)(completedAt - Now), 0.001f, duration);
        rig.BeginReload(duration, empty, duration - remaining);
        body.PlayReloadAnimation(duration, duration - remaining);
    }
    private void FinishElapsedReloadPresentation()
    {
        if (presentationReloadEnd <= 0 || Now < presentationReloadEnd) return;
        presentationReloadEnd = 0;
        rig.CancelReload();
        body.CancelActionAnimation();
    }
    [ServerRpc] private void FireServerRpc(int id, Vector3 requestedDirection, bool aimed)
    {
        var definition = ActiveDefinition;
        int ammo = slot.Value == 0 ? primaryAmmo.Value : secondaryAmmo.Value;
        double now = Now;
        double interval = definition != null ? definition.ShotInterval : 1d;
        // Retain at most one interval of credit so two normal shots batched by transport
        // can catch up. Accepted shots advance the schedule instead of resetting to arrival
        // time; the bounded credit cannot increase sustained rounds per minute.
        double scheduledAt = System.Math.Max(nextServerShot, now - interval);
        double jitterAllowance = System.Math.Min(0.04d, interval * 0.25d);
        bool valid = id > serverSequence && definition != null && CanFight() && !IsReloading && !IsSwitching &&
                     now + jitterAllowance >= scheduledAt && ammo > 0 && Finite(requestedDirection) && requestedDirection.sqrMagnitude > 0.5f && requestedDirection.sqrMagnitude < 1.5f;
        serverSequence = Mathf.Max(serverSequence, id);
        acknowledgedSequence.Value = serverSequence;
        if (!valid) return;
        nextServerShot = scheduledAt + interval;
        if (slot.Value == 0) primaryAmmo.Value--; else secondaryAmmo.Value--;
        Vector3 origin = rig.Muzzle != null ? rig.Muzzle.position : body.EyePosition;
        Vector3 baseDirection = ResolveFireDirection(body.EyePosition, origin, requestedDirection.normalized, definition.range);
        Vector3 direction = ApplySpray(baseDirection, aimed ? definition.aimSpray : definition.hipSpray, aimed ? aimMagnitude : hipMagnitude, burstIndex++);
        var shot = new Flight { origin = origin, direction = direction, position = origin, definition = definition };
        if (!HitBarrelObstruction(shot) && !HitAtOrigin(shot)) flights.Add(shot);
        ShotClientRpc(definition.weaponId, origin, direction);
    }
    [ClientRpc] private void ShotClientRpc(byte weaponId, Vector3 origin, Vector3 direction)
    {
        FinishElapsedReloadPresentation();
        if (!IsOwner && rig.ActiveWeaponId == weaponId) rig.PlayShot(false);
        if (Application.isBatchMode) return;
        var definition = PvpWeaponCatalog.Get(weaponId); if (definition == null) return;
        if (tracerMaterial == null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
            tracerMaterial = new Material(shader); tracerMaterial.color = new Color(1f, 0.75f, 0.35f);
        }
        var line = new GameObject("PVP projectile trace").AddComponent<LineRenderer>();
        line.sharedMaterial = tracerMaterial; line.positionCount = 2; line.startWidth = 0.012f; line.endWidth = 0.003f;
        line.SetPosition(0, origin); line.SetPosition(1, origin);
        visuals.Add(new VisualFlight { origin = origin, direction = direction, definition = definition, line = line });
    }
    private bool CanFight()
    {
        var lobby = LobbyManager.Instance != null ? LobbyManager.Instance.CurrentLobby : null;
        return health != null && !health.IsDead.Value && lobby != null && !lobby.IsPvpMatchOver;
    }
    private void Update()
    {
        if (!IsSpawned) return;
        FinishElapsedReloadPresentation();
        if (IsServer)
        {
            float speed = Vector3.Distance(transform.position, previousPosition) / Mathf.Max(0.001f, Time.deltaTime);
            previousPosition = transform.position;
            var d = ActiveDefinition;
            if (d != null)
            {
                UpdateSpray(d.hipSpray, ref hipMagnitude, speed);
                UpdateSpray(d.aimSpray, ref aimMagnitude, speed);
                if (!triggerHeld.Value && speed <= 0.7f && d.hipSpray != null && d.hipSpray.passiveMultiplier < 1f)
                    burstIndex = Mathf.RoundToInt(Mathf.InverseLerp(d.hipSpray.passiveMultiplier, 1f, hipMagnitude) * (d.hipSpray.points.Length - 1));
            }
        }
        if (IsServer && reloadAt.Value > 0 && Now >= reloadAt.Value)
        {
            var definition = ActiveDefinition;
            int ammo = slot.Value == 0 ? primaryAmmo.Value : secondaryAmmo.Value;
            int capacity = definition.magazineCapacity + (definition.keepChamberedRound && ammo > 0 ? 1 : 0);
            int count = Mathf.Min(capacity - ammo, ReserveAmmo);
            if (slot.Value == 0) { primaryAmmo.Value += count; primaryReserve.Value -= count; }
            else { secondaryAmmo.Value += count; secondaryReserve.Value -= count; }
            reloadAt.Value = 0;
        }
        for (int i = visuals.Count - 1; i >= 0; i--)
        {
            var visual = visuals[i]; visual.age += Time.deltaTime;
            Vector3 point = Position(visual.origin, visual.direction, visual.definition, visual.age);
            Vector3 prior = Position(visual.origin, visual.direction, visual.definition, Mathf.Max(0, visual.age - 0.012f));
            if (visual.age >= visual.definition.projectileLifetime || Vector3.Distance(visual.origin, point) >= visual.definition.range ||
                Physics.Linecast(visual.line.GetPosition(1), point, out RaycastHit hit, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) && !hit.transform.IsChildOf(transform))
            { Destroy(visual.line.gameObject); visuals.RemoveAt(i); continue; }
            visual.line.SetPosition(0, prior); visual.line.SetPosition(1, point); visuals[i] = visual;
        }
    }
    private void FixedUpdate()
    {
        if (!IsServer || !IsSpawned) return;
        for (int i = flights.Count - 1; i >= 0; i--)
        {
            var shot = flights[i]; shot.age += Time.fixedDeltaTime;
            Vector3 end = Position(shot.origin, shot.direction, shot.definition, shot.age);
            Vector3 step = end - shot.position;
            int count = Physics.SphereCastNonAlloc(shot.position, Mathf.Max(0.001f, shot.definition.hitRadius), step.normalized, hits, step.magnitude, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide);
            int nearest = -1; float nearestDistance = float.PositiveInfinity;
            for (int h = 0; h < count; h++)
            {
                var collider = hits[h].collider;
                if (collider == null || collider.transform.IsChildOf(transform) || collider.transform == transform) continue;
                if (collider.isTrigger && collider.GetComponent<PvpHeadHitbox>() == null) continue;
                if (hits[h].distance < nearestDistance) { nearest = h; nearestDistance = hits[h].distance; }
            }
            bool impacted = nearest >= 0;
            if (impacted)
            {
                var hit = hits[nearest];
                var victim = hit.collider.GetComponentInParent<PvpPlayerCombat>();
                bool head = hit.collider.GetComponent<PvpHeadHitbox>() != null;
                // The capsule encloses the head trigger; allow the same victim's head inside this step, never a target behind cover.
                if (victim != null && !head)
                    for (int h = 0; h < count; h++)
                    {
                        var hb = hits[h].collider != null ? hits[h].collider.GetComponent<PvpHeadHitbox>() : null;
                        if (hb != null && hb.Owner == victim) { head = true; hit = hits[h]; break; }
                    }
                if (victim != null) Damage(victim, head, hit.point, shot, shot.distance + hit.distance);
                else ImpactClientRpc(hit.point, hit.normal);
            }
            shot.distance += step.magnitude;
            if (impacted || shot.age >= shot.definition.projectileLifetime || shot.distance >= shot.definition.range) flights.RemoveAt(i);
            else { shot.position = end; flights[i] = shot; }
        }
    }
    private bool HitAtOrigin(Flight shot)
    {
        int count = Physics.OverlapSphereNonAlloc(shot.origin, Mathf.Max(0.005f, shot.definition.hitRadius), overlaps, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            var collider = overlaps[i]; if (collider == null || collider.transform == transform || collider.transform.IsChildOf(transform)) continue;
            var victim = collider.GetComponentInParent<PvpPlayerCombat>();
            if (victim != null) Damage(victim, false, shot.origin, shot, 0);
            return true;
        }
        return false;
    }
    private Vector3 ResolveFireDirection(Vector3 eye, Vector3 muzzle, Vector3 requested, float range)
    {
        int count = Physics.RaycastNonAlloc(eye, requested, hits, range, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        float nearest = float.PositiveInfinity;
        Vector3 target = eye + requested * range;
        for (int i = 0; i < count; i++)
        {
            if (hits[i].collider == null || hits[i].transform.IsChildOf(transform) || hits[i].distance >= nearest) continue;
            nearest = hits[i].distance; target = hits[i].point;
        }
        // All three Demo presets use FromMuzzleToCameraForward with convergence only beyond five metres.
        return nearest > 5f && !float.IsPositiveInfinity(nearest) ? (target - muzzle).normalized : requested;
    }
    private bool HitBarrelObstruction(Flight shot)
    {
        Vector3 eye = body.EyePosition;
        Vector3 offset = shot.origin - eye;
        if (offset.sqrMagnitude < 0.0001f) return false;
        if (!PvpBallistics.CastSegment(eye, shot.origin, shot.definition.hitRadius, Physics.DefaultRaycastLayers, transform, hits, out var hit)) return false;
        var victim = hit.collider.GetComponentInParent<PvpPlayerCombat>();
        if (victim != null) Damage(victim, false, hit.point, shot, 0);
        else ImpactClientRpc(hit.point, hit.normal);
        return true;
    }
    private void Damage(PvpPlayerCombat victim, bool head, Vector3 point, Flight shot, float distance)
    {
        float falloff = shot.definition.damageOverRange != null ? shot.definition.damageOverRange.Evaluate(Mathf.Clamp01(distance / shot.definition.range)) : 1;
        victim.ApplyProjectileDamage(health, shot.definition.damage * Mathf.Max(0, falloff) * (head ? shot.definition.headDamageMultiplier : 1f), head, point, shot.direction);
    }
    [ClientRpc] private void ImpactClientRpc(Vector3 point, Vector3 normal)
    {
        if (!Application.isBatchMode) PvpImpactEffects.Play(point, normal, false);
    }
    private static Vector3 Position(Vector3 origin, Vector3 direction, PvpWeaponDefinition definition, float time)
    {
        return PvpBallistics.GetPositionAtTime(origin, direction * definition.muzzleVelocity, time, definition.gravity, definition.drag);
    }
    private void ResetSpray(PvpWeaponDefinition definition)
    {
        burstIndex = 0;
        hipMagnitude = definition.hipSpray != null ? definition.hipSpray.passiveMultiplier : 0;
        aimMagnitude = definition.aimSpray != null ? definition.aimSpray.passiveMultiplier : 0;
    }
    private void UpdateSpray(PvpSprayDefinition spray, ref float magnitude, float speed)
    {
        if (spray == null) return;
        if (triggerHeld.Value) magnitude = Mathf.MoveTowards(magnitude, 1f, Time.deltaTime / Mathf.Max(0.001f, spray.rampUpTime));
        else if (speed <= 0.7f) magnitude = Mathf.MoveTowards(magnitude, spray.passiveMultiplier, Time.deltaTime / Mathf.Max(0.001f, spray.recoveryTime));
    }
    private static Vector3 ApplySpray(Vector3 direction, PvpSprayDefinition spray, float magnitude, int index)
    {
        if (spray == null || spray.points == null || spray.points.Length == 0) return direction;
        Quaternion rotation = Quaternion.LookRotation(direction);
        return PvpBallistics.ApplySpray(direction, rotation * Vector3.right, rotation * Vector3.up, spray, index, magnitude);
    }
    private static bool Finite(Vector3 v) => !float.IsNaN(v.x) && !float.IsInfinity(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
}
