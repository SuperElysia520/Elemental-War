using UnityEngine;

public static class PvpWeaponCatalog
{
    private static readonly string[] Names = { "AssaultRifle", "Pistol", "Sniper" };
    private static readonly PvpWeaponDefinition[] Cache = new PvpWeaponDefinition[3];

    public static PvpWeaponDefinition Get(byte weaponId)
    {
        int index = weaponId < Names.Length ? weaponId : 0;
        if (Cache[index] == null)
            Cache[index] = Resources.Load<PvpWeaponDefinition>("PvpWeapons/Definitions/" + Names[index]);
        return Cache[index];
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetCache()
    {
        for (int i = 0; i < Cache.Length; i++) Cache[i] = null;
    }
}
