using System.Text.Json.Serialization;

namespace Arcline.SteamGuard.Models;

public enum ConfirmationType
{
    Unknown = 0,
    Test = 1,
    Trade = 2,
    MarketListing = 3,
    FeatureOptOut = 4,
    PhoneNumberChange = 5,
    AccountRecovery = 6,
    ApiKeyRegistration = 9,
    JoinSteamFamily = 11,
    Purchase = 12,
}

/// <summary>One entry of <c>/mobileconf/getlist</c>.</summary>
public sealed class Confirmation
{
    [JsonPropertyName("id")] public ulong Id { get; set; }
    [JsonPropertyName("nonce")] public ulong Nonce { get; set; }
    [JsonPropertyName("creator_id")] public ulong CreatorId { get; set; }
    [JsonPropertyName("type")] public int RawType { get; set; }
    [JsonPropertyName("type_name")] public string? TypeName { get; set; }
    [JsonPropertyName("headline")] public string? Headline { get; set; }
    [JsonPropertyName("summary")] public List<string>? Summary { get; set; }
    [JsonPropertyName("accept")] public string? AcceptText { get; set; }
    [JsonPropertyName("cancel")] public string? CancelText { get; set; }
    [JsonPropertyName("icon")] public string? Icon { get; set; }
    [JsonPropertyName("creation_time")] public long CreationTime { get; set; }
    [JsonPropertyName("multi")] public bool Multi { get; set; }

    [JsonIgnore] public ConfirmationType Type => Enum.IsDefined(typeof(ConfirmationType), RawType) ? (ConfirmationType)RawType : ConfirmationType.Unknown;

    [JsonIgnore] public DateTimeOffset Created => DateTimeOffset.FromUnixTimeSeconds(CreationTime);

    [JsonIgnore] public string Description => Summary is { Count: > 0 } ? string.Join(Environment.NewLine, Summary) : string.Empty;

    public override string ToString() => $"[{Type}] {Headline} {Description}".Trim();
}

internal sealed class ConfirmationListResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("needauth")] public bool NeedAuth { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("detail")] public string? Detail { get; set; }
    [JsonPropertyName("conf")] public List<Confirmation>? Confirmations { get; set; }
}

internal sealed class SuccessResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}
