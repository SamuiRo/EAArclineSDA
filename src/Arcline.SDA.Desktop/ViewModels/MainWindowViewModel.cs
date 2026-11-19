using Arcline.SDA.Desktop.Services;
using Arcline.SteamGuard;
using Arcline.SteamGuard.Platform;
using Arcline.SteamGuard.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Arcline.SDA.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly Dialogs _dialogs;
    private readonly PassphraseStore _passphraseStore;
    private readonly AppSettings _settings;
    private string _vaultDir;

    [ObservableProperty] private ObservableObject _current;
    [ObservableProperty] private string _title = "Arcline SDA";

    public MainWindowViewModel(Dialogs dialogs, PassphraseStore? passphraseStore = null, bool tryAutoUnlock = true, AppSettings? settings = null)
    {
        _dialogs = dialogs;
        _passphraseStore = passphraseStore ?? new PassphraseStore();
        _settings = settings ?? AppSettings.Load();
        _vaultDir = ResolveVaultDir();
        _current = NewUnlock();
        if (tryAutoUnlock && MaFileVault.Exists(_vaultDir)) _ = TryAutoUnlockAsync();
    }

    public AppSettings Settings => _settings;
    public string VaultDir => _vaultDir;

    private UnlockViewModel NewUnlock() => new(_vaultDir, _dialogs, OnUnlocked, SwitchVault, _settings.ExistingRecentVaults());

    public AccountsViewModel? Accounts => Current as AccountsViewModel;

    public bool MinimizeToTray => Accounts?.Vault.Settings.MinimizeToTray == true;

    private static string ResolveVaultDir()
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(args, "--vault");
        return i >= 0 && i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : MaFileVault.DefaultDirectory();
    }

    /// <summary>Locks whatever is open and shows the unlock/create screen for another vault folder.</summary>
    public void SwitchVault(string dir)
    {
        if (Current is AccountsViewModel a) a.Dispose();
        _vaultDir = Path.GetFullPath(dir);
        Title = "Arcline SDA";
        Current = NewUnlock();
    }

    /// <summary>"Unlock automatically on this PC": only at startup, never after the user pressed Lock.</summary>
    private async Task TryAutoUnlockAsync()
    {
        var dir = _vaultDir;
        var vault = await Task.Run(() =>
        {
            var pass = _passphraseStore.TryLoad(dir);
            if (pass is null) return null;
            try
            {
                var v = MaFileVault.Open(dir);
                v.Unlock(pass);
                return v;
            }
            catch (SteamGuardException) { return null; } // passphrase changed elsewhere: fall back to the prompt
        });
        if (vault is null) return;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (Current is UnlockViewModel u && AppSettings.SamePath(u.VaultDir, dir)) OnUnlocked(vault);
            else vault.Dispose();
        });
    }

    private void OnUnlocked(MaFileVault vault)
    {
        _vaultDir = vault.Directory;
        _settings.RememberVault(vault.Directory);
        var vm = new AccountsViewModel(vault, _dialogs, Lock, t => Title = t, switchVault: SwitchVault, onUnlocked: OnUnlocked);
        Current = vm;
        vm.Start();
    }

    private void Lock()
    {
        if (Current is AccountsViewModel a) a.Dispose();
        Title = "Arcline SDA";
        Current = NewUnlock();
    }
}
