using System.Text.Json.Serialization;

namespace Fps.AccountService;

// Keep these JSON fields compatible with Assets/Scripts/Customization/PlayerProfileData.cs.
public sealed record Appearance(byte OutfitId = 0, byte FaceId = 0,
    byte ClothColorId = 0, byte HairColorId = 0, bool Hat = true, bool Accessories = true,
    int WardrobeItems = 0, int WardrobeColors = 0)
{
    [JsonIgnore] public bool IsValid => OutfitId <= 2 && FaceId <= 4 && ClothColorId <= 4 && HairColorId <= 3 && (OutfitId != 2 || !Accessories) && ValidWardrobe();
    private bool ValidWardrobe()
    {
        if (WardrobeItems == 0) return WardrobeColors == 0;
        if ((WardrobeItems >> 16) != 1 || (WardrobeColors >> 24) != 0) return false;
        for (int i = 0; i < 8; i++)
            if (((WardrobeItems >> (2*i)) & 3) > (i == 3 ? 3 : 1) ||
                ((WardrobeColors >> (3*i)) & 7) > (i == 0 ? 3 : 4)) return false;
        return true;
    }
}

public sealed record Loadout(byte PrimaryWeaponId = 0, byte PrimarySightId = 0, byte SidearmSightId = 0)
{
    [JsonIgnore] public bool IsValid => PrimaryWeaponId is 0 or 2 && PrimarySightId <= 5 && SidearmSightId is 0 or 3;
}

public sealed record PersonalProfile(string DisplayName, byte Gender, string Signature)
{
    [JsonIgnore] public bool IsValid => DisplayName != null && Signature != null && DisplayName.Length <= 20 && DisplayName == DisplayName.Trim() &&
        Signature.Length <= 80 && Gender <= 2 && !(DisplayName + Signature).Any(c => char.IsControl(c) || c is '<' or '>');
}

public sealed record Profile(string AccountId, string Username, int Revision, Appearance Appearance, Loadout Loadout)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public MatchAdmission? Match { get; init; }
    public byte AvatarId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public PersonalProfile? Personal { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int PersonalRevision { get; init; }
    [JsonIgnore] public string DisplayName => !string.IsNullOrWhiteSpace(Personal?.DisplayName) ? Personal.DisplayName : Username;
}
public sealed record SaveAvatarRequest(byte AvatarId);
public sealed record Credentials(string? Username, string? Password);
public sealed record RegistrationCheckRequest(string? Username);
public sealed record RegistrationCheckResponse(bool Registered);
public sealed record RegistrationResponse(string Username);
public sealed record SaveProfileRequest(int Revision, Appearance? Appearance, Loadout? Loadout);
public sealed record AuthResponse(string AccessToken, long ExpiresAtUnixSeconds, Profile Profile);
public sealed record GameTicketResponse(string Ticket, long ExpiresAtUnixSeconds);
public sealed record RedeemTicketRequest(string? Ticket);
public sealed record ApiError(string Code, string Message);

public sealed record Session(string TokenHash, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt);
public sealed record Account(string Id, string Username, string PasswordHash, Profile Profile, List<Session> Sessions);
public sealed record Database(int FormatVersion, List<Account> Accounts);

public sealed class ApiException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}
