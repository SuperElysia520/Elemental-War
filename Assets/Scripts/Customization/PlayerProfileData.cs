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
    public string username;
    public int revision;
    public byte avatarId;
    public MatchAdmissionData match;
    public CharacterAppearanceData appearance = CharacterAppearanceData.Default;
    public PvpLoadoutData loadout = PvpLoadoutData.Default;
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
