using System.Text.Json.Serialization;
using Arcline.SteamGuard.Crypto;
using Arcline.SteamGuard.Models;

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

    /// <summary>
    /// 2.3.0 stored auto-confirm switches once for all accounts. They are now per account. When an old vault.json
    /// is loaded, the old global switches are copied to every account and then cleared.
    /// Returns true if anything changed (the caller saves).
    /// </summary>
    public bool MigrateLegacySettings()
    {
        var s = Settings;
        if (!(s.LegacyAutoConfirmTrades || s.LegacyAutoConfirmMarket || s.LegacyAutoConfirmPurchases)) return false;
        foreach (var a in Accounts)
        {
            a.AutoConfirm.Trades |= s.LegacyAutoConfirmTrades;
            a.AutoConfirm.Market |= s.LegacyAutoConfirmMarket;
            a.AutoConfirm.Purchases |= s.LegacyAutoConfirmPurchases;
        }
        s.LegacyAutoConfirmTrades = s.LegacyAutoConfirmMarket = s.LegacyAutoConfirmPurchases = false;
        return true;
    }
}

public sealed class VaultEntry
{
    [JsonPropertyName("steamid")] public ulong SteamId { get; set; }
    [JsonPropertyName("account_name")] public string? AccountName { get; set; }
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("trade_protection_acknowledged")] public bool TradeProtectionAcknowledged { get; set; }
    [JsonPropertyName("added_at")] public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("auto_confirm")] public AutoConfirmRules AutoConfirm { get; set; } = new();

    [JsonIgnore] public string DisplayName => AccountName ?? SteamId.ToString();
}

/// <summary>
/// What this account confirms on its own during background checks. Each type is opt-in separately.
/// Everything else (API keys, phone/recovery changes, family, unknown or future types) is never auto-confirmed.
/// </summary>
public sealed class AutoConfirmRules
{
    [JsonPropertyName("trades")] public bool Trades { get; set; }
    /// <summary>Market listings, i.e. selling items on the Steam Community Market.</summary>
    [JsonPropertyName("market")] public bool Market { get; set; }
    /// <summary>Purchase confirmations (Steam type 12).</summary>
    [JsonPropertyName("purchases")] public bool Purchases { get; set; }

    [JsonIgnore] public bool Any => Trades || Market || Purchases;

    public bool Allows(Confirmation c) => c.Type switch
    {
        ConfirmationType.Trade => Trades,
        ConfirmationType.MarketListing => Market,
        ConfirmationType.Purchase => Purchases,
        _ => false,
    };

    /// <summary>Short label for the UI, e.g. "Market, Purchases" or "Off".</summary>
    public override string ToString()
    {
        var parts = new List<string>(3);
        if (Trades) parts.Add("Trades");
        if (Market) parts.Add("Market");
        if (Purchases) parts.Add("Purchases");
        return parts.Count == 0 ? "Off" : string.Join(", ", parts);
    }
}

public sealed class VaultSettings
{
    [JsonPropertyName("periodic_checking")] public bool PeriodicChecking { get; set; }
    [JsonPropertyName("periodic_checking_interval_seconds")] public int PeriodicCheckingIntervalSeconds { get; set; } = 60;
    [JsonPropertyName("check_all_accounts")] public bool CheckAllAccounts { get; set; }
    [JsonPropertyName("minimize_to_tray")] public bool MinimizeToTray { get; set; }

    // Pre-2.5.0 global auto-confirm switches; read only for migration (see VaultIndex.MigrateLegacySettings).
    [JsonPropertyName("auto_confirm_market")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool LegacyAutoConfirmMarket { get; set; }
    [JsonPropertyName("auto_confirm_trades")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool LegacyAutoConfirmTrades { get; set; }
    [JsonPropertyName("auto_confirm_purchases")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool LegacyAutoConfirmPurchases { get; set; }

    public int EffectiveIntervalSeconds => Math.Clamp(PeriodicCheckingIntervalSeconds, 15, 3600);
}
