using System.Text.Json.Serialization;

namespace Fps.AccountService;

// Keep these JSON fields compatible with Assets/Scripts/Customization/PlayerProfileData.cs.
public sealed record Appearance(byte OutfitId = 0, byte FaceId = 0,
    byte ClothColorId = 0, byte HairColorId = 0, bool Hat = true, bool Accessories = true)
{
    [JsonIgnore] public bool IsValid => OutfitId <= 2 && FaceId <= 4 && ClothColorId <= 4 && HairColorId <= 3 && (OutfitId != 2 || !Accessories);
}

public sealed record Loadout(byte PrimaryWeaponId = 0, byte PrimarySightId = 0, byte SidearmSightId = 0)
{
    [JsonIgnore] public bool IsValid => PrimaryWeaponId is 0 or 2 && PrimarySightId <= 5 && SidearmSightId is 0 or 3;
}

public sealed record Profile(string AccountId, string Username, int Revision, Appearance Appearance, Loadout Loadout)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public MatchAdmission? Match { get; init; }
    public byte AvatarId { get; init; }
}
public sealed record SaveAvatarRequest(byte AvatarId);
public sealed record Credentials(string? Username, string? Password);
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
