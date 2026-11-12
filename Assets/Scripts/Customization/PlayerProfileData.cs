using System;
using Unity.Netcode;

[Serializable]
public struct CharacterAppearanceData : INetworkSerializable, IEquatable<CharacterAppearanceData>
{
    public byte outfitId;
    public byte faceId;
    public byte clothColorId;
    public byte hairColorId;
    public bool hat;
    public bool accessories;

    public static CharacterAppearanceData Default => new CharacterAppearanceData { hat = true, accessories = true };

    public CharacterAppearanceData Sanitized()
    {
        var result = this;
        if (result.outfitId > 2) result.outfitId = 0;
        if (result.faceId > 4) result.faceId = 0;
        if (result.clothColorId > 4) result.clothColorId = 0;
        if (result.hairColorId > 3) result.hairColorId = 0;
        if (result.outfitId == 2) result.accessories = false;
        return result;
    }

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref outfitId);
        serializer.SerializeValue(ref faceId);
        serializer.SerializeValue(ref clothColorId);
        serializer.SerializeValue(ref hairColorId);
        serializer.SerializeValue(ref hat);
        serializer.SerializeValue(ref accessories);
    }

    public bool Equals(CharacterAppearanceData other) => outfitId == other.outfitId && faceId == other.faceId &&
        clothColorId == other.clothColorId && hairColorId == other.hairColorId && hat == other.hat && accessories == other.accessories;
    public override bool Equals(object obj) => obj is CharacterAppearanceData other && Equals(other);
    public override int GetHashCode() => outfitId | (faceId << 2) | (clothColorId << 5) | (hairColorId << 8) | (hat ? 1024 : 0) | (accessories ? 2048 : 0);
}

[Serializable]
public struct PvpLoadoutData : INetworkSerializable, IEquatable<PvpLoadoutData>
{
    public byte primaryWeaponId;
    public byte primarySightId;
    public byte sidearmSightId;
    public static PvpLoadoutData Default => new PvpLoadoutData();
    public PvpLoadoutData Sanitized()
    {
        var result = this;
        if (result.primaryWeaponId != 0 && result.primaryWeaponId != 2) result.primaryWeaponId = 0;
        if (result.primarySightId > 5) result.primarySightId = 0;
        if (result.sidearmSightId != 0 && result.sidearmSightId != 3) result.sidearmSightId = 0;
        return result;
    }
    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref primaryWeaponId);
        serializer.SerializeValue(ref primarySightId);
        serializer.SerializeValue(ref sidearmSightId);
    }
    public bool Equals(PvpLoadoutData other) => primaryWeaponId == other.primaryWeaponId && primarySightId == other.primarySightId && sidearmSightId == other.sidearmSightId;
    public override bool Equals(object obj) => obj is PvpLoadoutData other && Equals(other);
    public override int GetHashCode() => primaryWeaponId | (primarySightId << 2) | (sidearmSightId << 5);
}

[Serializable]
public class PlayerProfileData
{
    public string accountId;
    // Public display identity is separate from the Authentication / network accountId.
    public int uid;
    public string username;
    public int revision;
    public byte avatarId;
    public PersonalProfileData personal;
    public int personalRevision;
    public PlayerCareerRecord career;
    public string DisplayName => personal != null && !string.IsNullOrWhiteSpace(personal.displayName) ? personal.displayName : username;
    public MatchAdmissionData match;
    public CharacterAppearanceData appearance = CharacterAppearanceData.Default;
    public PvpLoadoutData loadout = PvpLoadoutData.Default;
}

[Serializable]
public sealed class PersonalProfileData
{
    public string displayName = "";
    // 0 = unspecified, 1 = male, 2 = female. Existing profiles remain unspecified.
    public byte gender;
    public string signature = "";

    public static bool IsValid(PersonalProfileData value)
    {
        // Read legacy cloud names without locking out existing accounts. New names
        // are checked by DisplayNameRules at the save boundary.
        if (value == null || value.gender > 2 || value.displayName == null || value.signature == null ||
            value.displayName.Length > 20 || value.signature.Length > 80 || value.displayName != value.displayName.Trim()) return false;
        foreach (char c in value.displayName) if (char.IsControl(c) || c == '<' || c == '>') return false;
        foreach (char c in value.signature) if (char.IsControl(c) || c == '<' || c == '>') return false;
        return true;
    }
}

[Serializable]
public sealed class PlayerCareerRecord
{
    public int matches, wins, kills, deaths, headshotKills;
    public bool IsValid => matches >= 0 && wins >= 0 && wins <= matches && kills >= 0 && deaths >= 0 && headshotKills >= 0 && headshotKills <= kills;
}


public static class AccountUid
{
    public const int First = 111100001;
    public const int Last = 999999999;
    // Preserve accounts registered before this feature. Keep this cutover fixed in later builds.
    public static readonly DateTime EnabledAtUtc = new DateTime(2026, 11, 10, 1, 21, 16, DateTimeKind.Utc);
    public static bool IsLegacyRegistration(DateTime createdAtUtc) => createdAtUtc.ToUniversalTime() < EnabledAtUtc;
    public static bool IsValid(int value) => value >= First && value <= Last;
    public static string Format(int value, string legacyId = null) => IsValid(value) ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : value == 0 ? legacyId ?? "" : "";
}

[Serializable]
public sealed class MatchRosterMemberData
{
    public string accountId;
    public byte team;
}

[Serializable]
public sealed class MatchAdmissionData
{
    public string matchId;
    public int serverPort;
    public MatchRosterMemberData[] roster;
}
