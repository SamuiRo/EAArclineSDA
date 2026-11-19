using Arcline.SteamGuard.Platform;
using Microsoft.Win32;

namespace Arcline.SteamGuard.Tests;

/// <summary>OS integration, each test on the OS it applies to (CI runs Windows and Ubuntu). Never touches real settings.</summary>
public class PlatformTests
{
    private static string FakeExe()
    {
        var exe = Path.Combine(TestData.TempDir(), OperatingSystem.IsWindows() ? "ArclineSDA.exe" : "ArclineSDA");
        File.WriteAllText(exe, "");
        return exe;
    }

    [Fact]
    public void Autostart_EnableDisable_RoundTrips()
    {
        if (!Autostart.IsSupported) return;
        var scratchKey = @"Software\ArclineSDA-Tests\" + Guid.NewGuid().ToString("N");
        var autostart = new Autostart(windowsKey: scratchKey, linuxAutostartDir: Path.Combine(TestData.TempDir(), "autostart"));
        var exe = FakeExe();
        try
        {
            Assert.False(autostart.IsEnabled());
            autostart.Enable(exe);
            Assert.True(autostart.IsEnabled());
            Assert.True(autostart.IsEnabledForThisExecutable(exe));
            Assert.False(autostart.IsEnabledForThisExecutable(exe + "-moved"));
            if (OperatingSystem.IsLinux())
            {
                var desktop = File.ReadAllText(autostart.LinuxDesktopFile);
                Assert.Contains($"Exec=\"{exe}\" --minimized", desktop);
            }
            autostart.Disable();
            Assert.False(autostart.IsEnabled());
        }
        finally
        {
            if (OperatingSystem.IsWindows()) Registry.CurrentUser.DeleteSubKeyTree(@"Software\ArclineSDA-Tests", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Autostart_RefusesMissingExecutable()
    {
        if (!Autostart.IsSupported) return;
        var autostart = new Autostart(windowsKey: @"Software\ArclineSDA-Tests\x", linuxAutostartDir: TestData.TempDir());
        Assert.Throws<SteamGuardException>(() => autostart.Enable(Path.Combine(TestData.TempDir(), "nope.exe")));
    }

    [Fact]
    public void PassphraseStore_Windows_DpapiRoundTrip_IsBoundToTheVault()
    {
        if (!OperatingSystem.IsWindows()) return;
        var file = Path.Combine(TestData.TempDir(), "autounlock.bin");
        var store = new PassphraseStore(file);
        var vaultA = TestData.TempDir();
        var vaultB = TestData.TempDir();

        Assert.Null(store.TryLoad(vaultA));
        store.Save(vaultA, "my vault passphrase");
        Assert.Equal("my vault passphrase", store.TryLoad(vaultA));
        Assert.Null(store.TryLoad(vaultB)); // stored for a different vault path
        Assert.DoesNotContain("my vault passphrase", File.ReadAllText(file)); // DPAPI-protected, not plaintext

        store.Remove(vaultA);
        Assert.Null(store.TryLoad(vaultA));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void AppPaths_AreUnderTheUserProfile()
    {
        Assert.EndsWith("ArclineSDA", AppPaths.Root, StringComparison.Ordinal);
        Assert.StartsWith(AppPaths.Root, AppPaths.Logs, StringComparison.Ordinal);
        Assert.StartsWith(AppPaths.Root, AppPaths.DefaultVault, StringComparison.Ordinal);
        Assert.All(AppPaths.LegacyExtractionDirs(), d => Assert.Contains(".net", d, StringComparison.Ordinal));
    }
}
