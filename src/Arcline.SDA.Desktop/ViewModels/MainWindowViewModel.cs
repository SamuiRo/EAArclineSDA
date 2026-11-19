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
    private readonly string _vaultDir;
    private readonly PassphraseStore _passphraseStore;

    [ObservableProperty] private ObservableObject _current;
    [ObservableProperty] private string _title = "Arcline SDA";

    public MainWindowViewModel(Dialogs dialogs, PassphraseStore? passphraseStore = null, bool tryAutoUnlock = true)
    {
        _dialogs = dialogs;
        _passphraseStore = passphraseStore ?? new PassphraseStore();
        _vaultDir = ResolveVaultDir();
        _current = NewUnlock();
        if (tryAutoUnlock && MaFileVault.Exists(_vaultDir)) _ = TryAutoUnlockAsync();
    }

    private UnlockViewModel NewUnlock() => new(_vaultDir, _dialogs, OnUnlocked, () => Current = NewUnlock());

    public AccountsViewModel? Accounts => Current as AccountsViewModel;

    public bool MinimizeToTray => Accounts?.Vault.Settings.MinimizeToTray == true;

    private static string ResolveVaultDir()
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(args, "--vault");
        return i >= 0 && i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : MaFileVault.DefaultDirectory();
    }

    /// <summary>"Unlock automatically on this PC": only at startup, never after the user pressed Lock.</summary>
    private async Task TryAutoUnlockAsync()
    {
        var vault = await Task.Run(() =>
        {
            var pass = _passphraseStore.TryLoad(_vaultDir);
            if (pass is null) return null;
            try
            {
                var v = MaFileVault.Open(_vaultDir);
                v.Unlock(pass);
                return v;
            }
            catch (SteamGuardException) { return null; } // passphrase changed elsewhere: fall back to the prompt
        });
        if (vault is null) return;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (Current is UnlockViewModel) OnUnlocked(vault);
            else vault.Dispose();
        });
    }

    private void OnUnlocked(MaFileVault vault)
    {
        var vm = new AccountsViewModel(vault, _dialogs, Lock, t => Title = t);
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
