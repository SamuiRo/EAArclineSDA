using Arcline.SteamGuard.Crypto;
using Arcline.SteamGuard.Platform;
using Arcline.SteamGuard.Storage;

namespace Arcline.SteamGuard.Tests;

/// <summary>Vaults in any folder (like KeePassXC): app settings with last/recent vaults, and moving a vault.</summary>
public class VaultLocationTests
{
    private const string Pass = "location test pass";

    [Fact]
    public void AppSettings_DefaultsWhenMissing_AndRoundTrips()
    {
        var file = Path.Combine(TestData.TempDir(), "settings.json");
        var s = AppSettings.Load(file);
        Assert.Equal(AppTheme.System, s.Theme);
        Assert.Null(s.LastVault);

        s.Theme = AppTheme.Dark;
        s.RememberVault(Path.Combine(TestData.TempDir(), "v1"));
        var back = AppSettings.Load(file);
        Assert.Equal(AppTheme.Dark, back.Theme);
        Assert.Equal(s.LastVault, back.LastVault);
        Assert.Contains("\"dark\"", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecentVaults_AreMostRecentFirst_Deduplicated_AndCapped()
    {
        var s = AppSettings.Load(Path.Combine(TestData.TempDir(), "settings.json"));
        var root = TestData.TempDir();
        for (var i = 0; i < AppSettings.MaxRecentVaults + 3; i++) s.RememberVault(Path.Combine(root, "v" + i));
        s.RememberVault(Path.Combine(root, "v2"));

        Assert.Equal(AppSettings.MaxRecentVaults, s.RecentVaults.Count);
        Assert.True(AppSettings.SamePath(Path.Combine(root, "v2"), s.RecentVaults[0]));
        Assert.Single(s.RecentVaults, v => AppSettings.SamePath(v, Path.Combine(root, "v2")));
        Assert.True(AppSettings.SamePath(Path.Combine(root, "v2"), s.LastVault!));

        s.ForgetVault(Path.Combine(root, "v2"));
        Assert.DoesNotContain(s.RecentVaults, v => AppSettings.SamePath(v, Path.Combine(root, "v2")));
        Assert.Equal(s.RecentVaults[0], s.LastVault);
    }

    [Fact]
    public void ExistingRecentVaults_SkipsMissingFolders()
    {
        var s = AppSettings.Load(Path.Combine(TestData.TempDir(), "settings.json"));
        var real = TestData.TempDir();
        MaFileVault.Create(real, Pass, MaFileEnvelope.MinIterations).Dispose();
        s.RememberVault(Path.Combine(TestData.TempDir(), "unplugged-usb"));
        s.RememberVault(real);
        Assert.Single(s.ExistingRecentVaults());
        Assert.Equal(2, s.RecentVaults.Count); // missing ones are kept, just not offered
    }

    [Fact]
    public void Move_CopiesEverything_VerifiesAndCleansTheOldFolder()
    {
        var from = TestData.TempDir();
        var acc = TestData.Account(7, "mover");
        using (var v = MaFileVault.Create(from, Pass, MaFileEnvelope.MinIterations))
        {
            v.Save(acc);
            v.Save(TestData.Account(8, "removed"));
            v.Remove(8, keepBackup: true); // creates backups/
        }
        File.WriteAllText(Path.Combine(from, "notes.txt"), "not ours");
        var to = Path.Combine(TestData.TempDir(), "new place");

        MaFileVault.Move(from, to);

        using var moved = MaFileVault.Open(to);
        moved.Unlock(Pass);
        Assert.Equal(acc.SharedSecret, moved.Load(7).SharedSecret);
        Assert.Single(Directory.GetFiles(Path.Combine(to, "backups")));
        Assert.False(MaFileVault.Exists(from));
        Assert.False(File.Exists(Path.Combine(from, "7.maFile")));
        Assert.True(File.Exists(Path.Combine(from, "notes.txt"))); // unrelated files are left alone
    }

    [Fact]
    public void Move_RefusesOccupiedTargetOrSameFolder()
    {
        var a = TestData.TempDir();
        var b = TestData.TempDir();
        MaFileVault.Create(a, Pass, MaFileEnvelope.MinIterations).Dispose();
        MaFileVault.Create(b, Pass, MaFileEnvelope.MinIterations).Dispose();
        Assert.Throws<SteamGuardException>(() => MaFileVault.Move(a, b));
        Assert.Throws<SteamGuardException>(() => MaFileVault.Move(a, a));
        Assert.Throws<SteamGuardException>(() => MaFileVault.Move(a, Path.Combine(a, "inner")));
        Assert.True(MaFileVault.Exists(a));
    }
}
