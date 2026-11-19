using System.Text.Json.Nodes;
using Arcline.SteamGuard.Crypto;
using Arcline.SteamGuard.Models;
using Arcline.SteamGuard.Storage;

namespace Arcline.SteamGuard.Tests;

public class AutoConfirmPolicyTests
{
    private static Confirmation C(int type) => new() { RawType = type };

    [Fact]
    public void NothingIsAutoConfirmed_ByDefault()
    {
        var r = new AutoConfirmRules();
        Assert.False(r.Any);
        Assert.Equal("Off", r.ToString());
        foreach (var t in Enum.GetValues<ConfirmationType>()) Assert.False(r.Allows(C((int)t)));
    }

    [Theory]
    [InlineData(ConfirmationType.Trade)]
    [InlineData(ConfirmationType.MarketListing)]
    [InlineData(ConfirmationType.Purchase)]
    public void EachSwitch_EnablesOnlyItsOwnType(ConfirmationType enabled)
    {
        var r = new AutoConfirmRules
        {
            Trades = enabled == ConfirmationType.Trade,
            Market = enabled == ConfirmationType.MarketListing,
            Purchases = enabled == ConfirmationType.Purchase,
        };
        foreach (var t in Enum.GetValues<ConfirmationType>()) Assert.Equal(t == enabled, r.Allows(C((int)t)));
    }

    [Theory]
    [InlineData(ConfirmationType.ApiKeyRegistration)]
    [InlineData(ConfirmationType.PhoneNumberChange)]
    [InlineData(ConfirmationType.AccountRecovery)]
    [InlineData(ConfirmationType.Unknown)]
    public void SensitiveOrUnknownTypes_AreNeverAutoConfirmed(ConfirmationType t)
    {
        var r = new AutoConfirmRules { Trades = true, Market = true, Purchases = true };
        Assert.False(r.Allows(C((int)t)));
        Assert.False(r.Allows(C(999))); // a type Steam adds in the future
    }

    [Fact]
    public void Rules_ArePerAccount_AndPersisted()
    {
        var dir = TestData.TempDir();
        using (var v = MaFileVault.Create(dir, "per-account-test", MaFileEnvelope.MinIterations))
        {
            v.Save(TestData.Account(1, "a"));
            v.Save(TestData.Account(2, "b"));
            v.SetAutoConfirm(1, new AutoConfirmRules { Market = true });
            Assert.True(v.Settings.PeriodicChecking); // enabling a rule turns background checking on
        }
        using var vault = MaFileVault.Open(dir);
        Assert.True(vault.ShouldAutoConfirm(1, C((int)ConfirmationType.MarketListing)));
        Assert.False(vault.ShouldAutoConfirm(1, C((int)ConfirmationType.Trade)));
        Assert.False(vault.ShouldAutoConfirm(2, C((int)ConfirmationType.MarketListing)));
        Assert.Equal("Market", vault.GetEntry(1)!.AutoConfirm.ToString());
    }

    [Fact]
    public void OldGlobalSwitches_AreMigratedToEveryAccount_ThenRemoved()
    {
        var dir = TestData.TempDir();
        using (var v = MaFileVault.Create(dir, "migration-test", MaFileEnvelope.MinIterations))
        {
            v.Save(TestData.Account(1, "a"));
            v.Save(TestData.Account(2, "b"));
        }
        // Simulate a 2.3.0 vault.json: global switches, no per-account rules.
        var path = Path.Combine(dir, "vault.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        node["settings"]!["auto_confirm_market"] = true;
        node["settings"]!["auto_confirm_purchases"] = true;
        foreach (var a in node["accounts"]!.AsArray()) a!.AsObject().Remove("auto_confirm");
        File.WriteAllText(path, node.ToJsonString());

        using (var vault = MaFileVault.Open(dir))
        {
            Assert.Equal("Market, Purchases", vault.GetEntry(1)!.AutoConfirm.ToString());
            Assert.Equal("Market, Purchases", vault.GetEntry(2)!.AutoConfirm.ToString());
        }
        var saved = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Null(saved["settings"]!["auto_confirm_market"]); // old keys are gone after migration
    }
}
