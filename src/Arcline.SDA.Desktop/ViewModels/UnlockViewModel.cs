using Arcline.SDA.Desktop.Services;
using Arcline.SteamGuard;
using Arcline.SteamGuard.Platform;
using Arcline.SteamGuard.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arcline.SDA.Desktop.ViewModels;

/// <summary>
/// First screen: unlock the vault, or create one. Like KeePassXC, a vault can live in any folder:
/// open another one, create one elsewhere, or pick a recent one.
/// </summary>
/// <param name="switchVault">Shows this screen for another folder (the app then remembers it).</param>
public sealed partial class UnlockViewModel(string vaultDir, Dialogs dialogs, Action<MaFileVault> onUnlocked, Action<string> switchVault,
    IReadOnlyList<string> recentVaults) : ObservableObject
{
    public string VaultDir { get; } = vaultDir;
    public bool IsNew { get; } = !MaFileVault.Exists(vaultDir);
    public bool IsExisting => !IsNew;

    /// <summary>Other vaults opened before (current one excluded), most recent first.</summary>
    public IReadOnlyList<string> RecentVaults { get; } = recentVaults.Where(v => !AppSettings.SamePath(v, vaultDir)).ToList();
    public bool HasRecentVaults => RecentVaults.Count > 0;

    public string Heading => IsNew ? "Create your encrypted vault" : "Unlock vault";

    public string Explanation => IsNew
        ? "Every maFile is stored encrypted (AES-256-GCM). Choose a passphrase you will not forget: without it the vault cannot be opened, and nobody can recover it."
        : "Enter your vault passphrase.";

    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(SubmitCommand))] private string _passphrase = "";
    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(SubmitCommand))] private string _confirm = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(SubmitCommand))] private bool _busy;

    private bool CanSubmit() => !Busy && Passphrase.Length > 0 && (!IsNew || Confirm.Length > 0);

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync()
    {
        Error = null;
        if (IsNew)
        {
            if (Passphrase != Confirm) { Error = "Passphrases do not match."; return; }
            if (Passphrase.Length < MaFileVault.MinPassphraseLength) { Error = $"Use at least {MaFileVault.MinPassphraseLength} characters."; return; }
        }
        Busy = true;
        try
        {
            var pass = Passphrase;
            // PBKDF2 takes a moment: keep it off the UI thread.
            var vault = await Task.Run(() =>
            {
                if (IsNew) return MaFileVault.Create(VaultDir, pass);
                var v = MaFileVault.Open(VaultDir);
                v.Unlock(pass);
                return v;
            });
            Passphrase = Confirm = "";
            onUnlocked(vault);
        }
        catch (WrongPassphraseException) { Error = "Wrong passphrase."; }
        catch (SteamGuardException ex) { Error = ex.Message; }
        catch (IOException ex) { Error = ex.Message; }
        catch (UnauthorizedAccessException ex) { Error = ex.Message; }
        finally { Busy = false; }
    }

    /// <summary>Open a vault in any folder (USB stick, synced folder, ...).</summary>
    [RelayCommand]
    private async Task OpenOtherVaultAsync()
    {
        var dir = await dialogs.PickFolderAsync("Select a vault folder (it contains vault.json)");
        if (dir is null) return;
        if (MaFileVault.Exists(dir)) { switchVault(dir); return; }
        if (await dialogs.ConfirmAsync("Open vault", $"There is no Arcline vault in:\n{dir}\n\nCreate a new vault there?", "Create here", "Cancel"))
            switchVault(dir);
    }

    /// <summary>Create a new, empty vault in a folder of your choice.</summary>
    [RelayCommand]
    private async Task CreateVaultElsewhereAsync()
    {
        var dir = await dialogs.PickFolderAsync("Select an empty folder for the new vault");
        if (dir is null) return;
        if (MaFileVault.Exists(dir))
        {
            await dialogs.AlertAsync("Create vault", "That folder already contains a vault; it will be opened instead.");
        }
        switchVault(dir);
    }

    [RelayCommand]
    private void OpenRecent(string dir) => switchVault(dir);

    /// <summary>New PC: put a backup (.zip or copied vault folder) in place, then unlock it with its old passphrase.</summary>
    [RelayCommand]
    private async Task RestoreAsync()
    {
        var kind = await dialogs.ChooseAsync("Restore from backup",
            $"Restore a vault made on another PC (File > Back up vault), or a copied vault folder, into:\n{VaultDir}\n\nYou will unlock it with the same passphrase as before.",
            "Backup .zip", "Vault folder");
        if (kind < 0) return;
        var source = kind == 0 ? await dialogs.OpenBackupAsync("Select vault backup") : await dialogs.PickFolderAsync("Select the vault folder (contains vault.json)");
        if (source is null) return;
        try
        {
            await Task.Run(() => MaFileVault.Restore(source, VaultDir));
            switchVault(VaultDir);
        }
        catch (SteamGuardException ex) { Error = ex.Message; }
        catch (IOException ex) { Error = ex.Message; }
    }
}
