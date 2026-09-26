using Unity.Netcode;
using UnityEngine;

/// <summary>Server-authenticated appearance and loadout snapshot, retained through respawns.</summary>
public sealed class PvpCharacterProfile : NetworkBehaviour
{
    public readonly NetworkVariable<CharacterAppearanceData> Appearance = new NetworkVariable<CharacterAppearanceData>(CharacterAppearanceData.Default);
    public readonly NetworkVariable<PvpLoadoutData> Loadout = new NetworkVariable<PvpLoadoutData>(PvpLoadoutData.Default);
    private RifleGirlAppearance view;

    public void ConfigureBeforeSpawn(PlayerProfileData profile)
    {
        if (IsSpawned || profile == null) return;
        Appearance.Reset(profile.appearance.Sanitized());
        Loadout.Reset(profile.loadout.Sanitized());
    }
    public override void OnNetworkSpawn()
    {
        view = GetComponentInChildren<RifleGirlAppearance>();
        Appearance.OnValueChanged += OnAppearanceChanged;
        ApplyAppearance();
    }
    public override void OnNetworkDespawn() { Appearance.OnValueChanged -= OnAppearanceChanged; }
    private void OnAppearanceChanged(CharacterAppearanceData previous, CharacterAppearanceData next) { ApplyAppearance(); }
    public void ApplyAppearance()
    {
        if (view == null) view = GetComponentInChildren<RifleGirlAppearance>();
        if (view == null) return;
        view.Apply(Appearance.Value);
        view.SetLocalFirstPerson(IsOwner);
    }
}
