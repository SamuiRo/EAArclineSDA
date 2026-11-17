using System.Security.Cryptography;
using Arcline.SDA.Desktop;
using Arcline.SDA.Desktop.Services;
using Arcline.SDA.Desktop.ViewModels;
using Arcline.SDA.Desktop.Views;
using Arcline.SteamGuard.Models;
using Arcline.SteamGuard.Storage;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

[assembly: AvaloniaTestApplication(typeof(Arcline.SDA.Desktop.Tests.TestAppBuilder))]

namespace Arcline.SDA.Desktop.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>Renders the real views headlessly (no desktop needed) and checks they bind and behave.</summary>
public class UiSmokeTests
{
    private static readonly string ShotsDir = Path.Combine(AppContext.BaseDirectory, "screenshots");

    private static MaFileVault NewVault(int accounts)
    {
        var dir = Path.Combine(Path.GetTempPath(), "arcline-ui-tests", Guid.NewGuid().ToString("N"));
        var vault = MaFileVault.Create(dir, "ui-test-passphrase", 100_000);
        for (var i = 0; i < accounts; i++)
        {
            vault.Save(new SteamGuardAccount
            {
                AccountName = $"account{i + 1}",
                SharedSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(20)),
                IdentitySecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(20)),
                DeviceId = "android:test",
                RevocationCode = "R00000",
                Session = new SessionData { SteamId = 76561198000000000UL + (ulong)i, AccessToken = "x.y.z", RefreshToken = "x.y.z" },
            });
        }
        return vault;
    }

    private static void Save(MainWindow w, string name)
    {
        Directory.CreateDirectory(ShotsDir);
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame()?.Save(Path.Combine(ShotsDir, name));
    }

    [AvaloniaFact]
    public void UnlockScreen_CreateMode_Renders()
    {
        var w = new MainWindow();
        var dir = Path.Combine(Path.GetTempPath(), "arcline-ui-tests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("ARCLINE_SDA_VAULT", dir);
        try
        {
            var vm = new MainWindowViewModel(new Dialogs(() => w));
            w.DataContext = vm;
            w.Show();
            var unlock = Assert.IsType<UnlockViewModel>(vm.Current);
            Assert.True(unlock.IsNew);
            Assert.False(unlock.SubmitCommand.CanExecute(null));
            unlock.Passphrase = "abc";
            unlock.Confirm = "abc";
            Assert.True(unlock.SubmitCommand.CanExecute(null));
            Save(w, "unlock-create.png");
        }
        finally { Environment.SetEnvironmentVariable("ARCLINE_SDA_VAULT", null); }
    }

    [AvaloniaFact]
    public void AccountsScreen_ShowsAccountsAndLiveCode()
    {
        var w = new MainWindow { Width = 900, Height = 620 };
        var vault = NewVault(3);
        var vm = new AccountsViewModel(vault, new Dialogs(() => w), () => { }, t => w.Title = t);
        w.Content = new AccountsView { DataContext = vm };
        w.Show();
        vm.Start();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, vm.Accounts.Count);
        Assert.NotNull(vm.Selected);
        Assert.Matches("^[23456789BCDFGHJKMNPQRTVWXY]{5}$", vm.Code);
        Save(w, "accounts.png");

        vm.Search = "account2";
        Assert.Single(vm.FilteredAccounts);
        Assert.Equal("account2", vm.Selected?.Name); // selection follows the filter
        vm.Search = "";
        Assert.Equal(3, vm.FilteredAccounts.Count);
        Assert.Equal("account2", vm.Selected?.Name); // and is kept when the filter is cleared
        Assert.True(vm.HasSelection);
        vm.Dispose();
    }
}
