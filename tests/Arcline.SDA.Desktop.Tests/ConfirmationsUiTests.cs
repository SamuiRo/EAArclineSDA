using System.Net;
using System.Security.Cryptography;
using System.Text;
using Arcline.SDA.Desktop.Services;
using Arcline.SDA.Desktop.ViewModels;
using Arcline.SDA.Desktop.Views;
using Arcline.SteamGuard;
using Arcline.SteamGuard.Models;
using Arcline.SteamGuard.Steam;
using Arcline.SteamGuard.Storage;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace Arcline.SDA.Desktop.Tests;

/// <summary>Selection, filtering, bulk actions and the all-accounts view, against a fake Steam.</summary>
public class ConfirmationsUiTests
{
    private sealed class FakeSteam : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add(path + " " + body);
            var steamId = request.RequestUri.Query.Contains("a=76561198000000001", StringComparison.Ordinal) ? 1 : 0;
            var json = path switch
            {
                "/ITwoFactorService/QueryTime/v1/" => $"{{\"response\":{{\"server_time\":\"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}\"}}}}",
                "/mobileconf/getlist" => steamId == 0
                    ? """{"success":true,"conf":[{"type":2,"id":"11","nonce":"1","creation_time":3,"headline":"Trade A"},{"type":2,"id":"12","nonce":"2","creation_time":2,"headline":"Trade B"},{"type":3,"id":"13","nonce":"3","creation_time":1,"headline":"Sell item"}]}"""
                    : """{"success":true,"conf":[{"type":2,"id":"21","nonce":"4","creation_time":5,"headline":"Trade C"}]}""",
                _ => "{\"success\":true}",
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static string Jwt(DateTimeOffset exp)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("{}")}.{B64($"{{\"exp\":{exp.ToUnixTimeSeconds()}}}")}.s";
    }

    private static (AccountsViewModel vm, FakeSteam fake, MainWindow w) Create()
    {
        var dir = Path.Combine(Path.GetTempPath(), "arcline-ui-tests", Guid.NewGuid().ToString("N"));
        var vault = MaFileVault.Create(dir, "ui-test-passphrase", 100_000);
        for (var i = 0; i < 2; i++)
        {
            vault.Save(new SteamGuardAccount
            {
                AccountName = $"acc{i}",
                SharedSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(20)),
                IdentitySecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(20)),
                DeviceId = "android:test",
                Session = new SessionData { SteamId = 76561198000000000UL + (ulong)i, AccessToken = Jwt(DateTimeOffset.UtcNow.AddHours(5)), RefreshToken = Jwt(DateTimeOffset.UtcNow.AddDays(100)) },
            });
        }
        var fake = new FakeSteam();
        var http = new SteamHttp(fake);
        var w = new MainWindow { Width = 900, Height = 640 };
        var vm = new AccountsViewModel(vault, new Dialogs(() => w), () => { }, _ => { }, new SteamGuardClient(vault, http, new SteamTime(http)));
        w.Content = new AccountsView { DataContext = vm };
        w.Show();
        return (vm, fake, w);
    }

    private static async Task Settle(AccountsViewModel? vm = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        do
        {
            for (var i = 0; i < 10; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(20);
            }
        } while (vm is { LoadingConfirmations: true } && DateTime.UtcNow < deadline);
    }

    [AvaloniaFact]
    public async Task FilterSelectAndAcceptSelected()
    {
        var (vm, fake, w) = Create();
        vm.Start();
        await Settle(vm);
        Assert.Equal(3, vm.Confirmations.Count);
        Assert.Equal("Confirmations (3)", vm.ConfirmationsHeader);
        Assert.False(vm.AcceptSelectedCommand.CanExecute(null));

        vm.Filter = ConfirmationFilter.Trades;
        Assert.Equal(2, vm.Confirmations.Count);
        Assert.Equal("Confirmations (2 of 3)", vm.ConfirmationsHeader);

        vm.AllSelected = true; // header checkbox
        Assert.Equal(2, vm.SelectedCount);
        Assert.True(vm.AllSelected);
        Assert.Equal("Accept selected (2)", vm.AcceptSelectedText);
        Assert.True(vm.AcceptSelectedCommand.CanExecute(null));

        vm.Confirmations[0].IsSelected = false;
        Assert.Null(vm.AllSelected); // partially selected
        vm.Confirmations[0].IsSelected = true;

        await vm.AcceptSelectedCommand.ExecuteAsync(null);
        await Settle(vm);

        // One batched call with both trade ids, preceded by the trade protection acknowledge.
        var multi = Assert.Single(fake.Calls, c => c.StartsWith("/mobileconf/multiajaxop", StringComparison.Ordinal));
        Assert.Contains("cid%5B%5D=11", multi);
        Assert.Contains("cid%5B%5D=12", multi);
        Assert.DoesNotContain("cid%5B%5D=13", multi);
        Assert.Contains(fake.Calls, c => c.StartsWith("/trade/new/acknowledge", StringComparison.Ordinal));

        Assert.Empty(vm.Confirmations);
        vm.Filter = ConfirmationFilter.All;
        var left = Assert.Single(vm.Confirmations);
        Assert.Equal(13UL, left.Id);
        Assert.False(left.IsSelected); // hidden items are never acted on or left selected
        Assert.Equal(1, vm.Selected!.Pending);
        vm.Dispose();
        w.Close();
    }

    [AvaloniaFact]
    public async Task AllAccountsMode_MergesAndRespondsPerAccount()
    {
        var (vm, fake, w) = Create();
        vm.Start();
        await Settle(vm);
        vm.ShowAllAccounts = true;
        await Settle(vm);

        Assert.Equal(4, vm.Confirmations.Count);
        Assert.All(vm.Confirmations, c => Assert.True(c.ShowAccount));
        Assert.Contains(vm.Confirmations, c => c.AccountName == "acc1" && c.Id == 21);

        vm.SelectAllCommand.Execute(null);
        Assert.Equal(4, vm.SelectedCount);
        await vm.AcceptSelectedCommand.ExecuteAsync(null);
        await Settle(vm);

        // One batched request per account.
        Assert.Equal(2, fake.Calls.Count(c => c.StartsWith("/mobileconf/multiajaxop", StringComparison.Ordinal)));
        Assert.Empty(vm.Confirmations);
        Assert.All(vm.Accounts, a => Assert.Equal(0, a.Pending));

        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "screenshots"));
        vm.ShowAllAccounts = false;
        await Settle(vm);
        vm.SelectAllCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame()?.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", "confirmations.png"));
        vm.Dispose();
        w.Close();
    }
}
