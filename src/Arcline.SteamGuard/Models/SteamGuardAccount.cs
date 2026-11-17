using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arcline.SteamGuard.Models;

/// <summary>
/// The decrypted contents of a maFile. Field names are byte-for-byte compatible with
/// Steam Desktop Authenticator / SteamAuth so exported files work in SDA, ASF and other tools.
/// Unknown fields are preserved through <see cref="Extra"/>.
/// </summary>
public sealed class SteamGuardAccount
{
    [JsonPropertyName("shared_secret")] public string? SharedSecret { get; set; }
    [JsonPropertyName("serial_number")] public string? SerialNumber { get; set; }
    [JsonPropertyName("revocation_code")] public string? RevocationCode { get; set; }
    [JsonPropertyName("uri")] public string? Uri { get; set; }
    [JsonPropertyName("server_time")] public long ServerTime { get; set; }
    [JsonPropertyName("account_name")] public string? AccountName { get; set; }
    [JsonPropertyName("token_gid")] public string? TokenGid { get; set; }
    [JsonPropertyName("identity_secret")] public string? IdentitySecret { get; set; }
    [JsonPropertyName("secret_1")] public string? Secret1 { get; set; }
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("device_id")] public string? DeviceId { get; set; }
    [JsonPropertyName("fully_enrolled")] public bool FullyEnrolled { get; set; }

    [JsonPropertyName("Session")] public SessionData? Session { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public ulong SteamId => Session?.SteamId ?? 0;

    [JsonIgnore] public string DisplayName => AccountName ?? SteamId.ToString();

    public static SteamGuardAccount Parse(string json)
    {
        var acc = JsonSerializer.Deserialize<SteamGuardAccount>(json, Json.Options)
                  ?? throw new SteamGuardException("maFile is empty.");
        if (string.IsNullOrEmpty(acc.SharedSecret))
            throw new SteamGuardException("Not a valid maFile: shared_secret is missing.");
        return acc;
    }

    public string ToJson(bool indented = true) =>
        JsonSerializer.Serialize(this, indented ? Json.Options : Json.Compact);

    public SteamGuardAccount Clone() => Parse(ToJson(false));
}

/// <summary>Web session tokens. PascalCase names match the SDA maFile "Session" object.</summary>
public sealed class SessionData
{
    [JsonPropertyName("SteamID")] public ulong SteamId { get; set; }
    [JsonPropertyName("AccessToken")] public string? AccessToken { get; set; }
    [JsonPropertyName("RefreshToken")] public string? RefreshToken { get; set; }
    [JsonPropertyName("SessionID")] public string? SessionId { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public bool IsAccessTokenExpired(TimeSpan? skew = null) =>
        Jwt.IsExpired(AccessToken, skew ?? TimeSpan.FromMinutes(2));

    public bool IsRefreshTokenExpired() => Jwt.IsExpired(RefreshToken, TimeSpan.Zero);

    public DateTimeOffset? RefreshTokenExpiresAt => Jwt.GetExpiry(RefreshToken);
}
