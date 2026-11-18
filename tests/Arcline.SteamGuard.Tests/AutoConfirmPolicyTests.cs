using System.Text.Json;
using Arcline.SteamGuard.Models;
using Arcline.SteamGuard.Storage;

namespace Arcline.SteamGuard.Tests;

public class AutoConfirmPolicyTests
{
    private static Confirmation C(int type) => new() { RawType = type };

    [Fact]
    public void NothingIsAutoConfirmed_ByDefault()
    {
        var s = new VaultSettings();
        Assert.False(s.AnyAutoConfirm);
        foreach (var t in Enum.GetValues<ConfirmationType>()) Assert.False(s.ShouldAutoConfirm(C((int)t)));
    }

    [Theory]
    [InlineData(ConfirmationType.Trade)]
    [InlineData(ConfirmationType.MarketListing)]
    [InlineData(ConfirmationType.Purchase)]
    public void EachSwitch_EnablesOnlyItsOwnType(ConfirmationType enabled)
    {
        var s = new VaultSettings
        {
            AutoConfirmTrades = enabled == ConfirmationType.Trade,
            AutoConfirmMarket = enabled == ConfirmationType.MarketListing,
            AutoConfirmPurchases = enabled == ConfirmationType.Purchase,
        };
        foreach (var t in Enum.GetValues<ConfirmationType>()) Assert.Equal(t == enabled, s.ShouldAutoConfirm(C((int)t)));
    }

    [Theory]
    [InlineData(ConfirmationType.ApiKeyRegistration)]
    [InlineData(ConfirmationType.PhoneNumberChange)]
    [InlineData(ConfirmationType.AccountRecovery)]
    [InlineData(ConfirmationType.Unknown)]
    public void SensitiveOrUnknownTypes_AreNeverAutoConfirmed(ConfirmationType t)
    {
        var s = new VaultSettings { AutoConfirmTrades = true, AutoConfirmMarket = true, AutoConfirmPurchases = true };
        Assert.False(s.ShouldAutoConfirm(C((int)t)));
        Assert.False(s.ShouldAutoConfirm(C(999))); // a type Steam adds in the future
    }

    [Fact]
    public void OldSettingsFiles_StillLoad_WithPurchasesOff()
    {
        var s = JsonSerializer.Deserialize<VaultSettings>("""{"auto_confirm_market":true,"auto_confirm_trades":false}""", Json.Options)!;
        Assert.True(s.AutoConfirmMarket);
        Assert.False(s.AutoConfirmPurchases);
    }
}
