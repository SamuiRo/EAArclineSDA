using System.Text.Json.Serialization;
using Arcline.SteamGuard.Crypto;

namespace Arcline.SteamGuard.Storage;

/// <summary>vault.json: non-secret index + settings. Secrets live only in the encrypted *.maFile envelopes.</summary>
public sealed class VaultIndex
{
    public const string FormatName = "arcline-vault";

    [JsonPropertyName("format")] public string Format { get; set; } = FormatName;
    [JsonPropertyName("version")] public int Version { get; set; } = 1;

    /// <summary>Shared salt/iterations for files written by this vault (lets the key be derived once).</summary>
    [JsonPropertyName("kdf")] public MaFileEnvelope.KdfParams Kdf { get; set; } = new();

    /// <summary>Encrypted known value used to check the passphrase even when the vault is empty.</summary>
    [JsonPropertyName("verifier")] public MaFileEnvelope? Verifier { get; set; }

    [JsonPropertyName("settings")] public VaultSettings Settings { get; set; } = new();

    [JsonPropertyName("accounts")] public List<VaultEntry> Accounts { get; set; } = [];
}

public sealed class VaultEntry
{
    [JsonPropertyName("steamid")] public ulong SteamId { get; set; }
    [JsonPropertyName("account_name")] public string? AccountName { get; set; }
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("trade_protection_acknowledged")] public bool TradeProtectionAcknowledged { get; set; }
    [JsonPropertyName("added_at")] public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore] public string DisplayName => AccountName ?? SteamId.ToString();
}

public sealed class VaultSettings
{
    [JsonPropertyName("periodic_checking")] public bool PeriodicChecking { get; set; }
    [JsonPropertyName("periodic_checking_interval_seconds")] public int PeriodicCheckingIntervalSeconds { get; set; } = 60;
    [JsonPropertyName("check_all_accounts")] public bool CheckAllAccounts { get; set; }
    /// <summary>Market listings, i.e. selling items on the Steam Community Market.</summary>
    [JsonPropertyName("auto_confirm_market")] public bool AutoConfirmMarket { get; set; }
    [JsonPropertyName("auto_confirm_trades")] public bool AutoConfirmTrades { get; set; }
    /// <summary>Purchase confirmations (Steam type 12).</summary>
    [JsonPropertyName("auto_confirm_purchases")] public bool AutoConfirmPurchases { get; set; }
    [JsonPropertyName("minimize_to_tray")] public bool MinimizeToTray { get; set; }

    public int EffectiveIntervalSeconds => Math.Clamp(PeriodicCheckingIntervalSeconds, 15, 3600);

    public bool AnyAutoConfirm => AutoConfirmTrades || AutoConfirmMarket || AutoConfirmPurchases;

    /// <summary>
    /// Whether background checking may accept this confirmation without asking. Each type is opt-in on its own;
    /// everything else (API keys, phone/recovery changes, family, unknown types) is never auto-confirmed.
    /// </summary>
    public bool ShouldAutoConfirm(Models.Confirmation c) => c.Type switch
    {
        Models.ConfirmationType.Trade => AutoConfirmTrades,
        Models.ConfirmationType.MarketListing => AutoConfirmMarket,
        Models.ConfirmationType.Purchase => AutoConfirmPurchases,
        _ => false,
    };
}
