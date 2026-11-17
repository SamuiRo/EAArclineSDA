using Arcline.SDA.Desktop.Services;
using Arcline.SteamGuard.Storage;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Arcline.SDA.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly Dialogs _dialogs;
    private readonly string _vaultDir;

    [ObservableProperty] private ObservableObject _current;
    [ObservableProperty] private string _title = "Arcline SDA";

    public MainWindowViewModel(Dialogs dialogs)
    {
        _dialogs = dialogs;
        _vaultDir = ResolveVaultDir();
        _current = NewUnlock();
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
