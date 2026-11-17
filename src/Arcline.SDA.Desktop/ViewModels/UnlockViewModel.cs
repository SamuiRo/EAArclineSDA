using Arcline.SDA.Desktop.Services;
using Arcline.SteamGuard;
using Arcline.SteamGuard.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arcline.SDA.Desktop.ViewModels;

/// <summary>First screen: create a new encrypted vault, or unlock the existing one.</summary>
public sealed partial class UnlockViewModel(string vaultDir, Dialogs dialogs, Action<MaFileVault> onUnlocked, Action onRestored) : ObservableObject
{
    /// <summary>New PC: put a backup (.zip or copied vault folder) in place, then unlock it with its old passphrase.</summary>
    [RelayCommand]
    private async Task RestoreAsync()
    {
        var kind = await dialogs.ChooseAsync("Restore from backup",
            "Restore a vault made on another PC (File > Back up vault), or a copied vault folder. You will unlock it with the same passphrase as before.",
            "Backup .zip", "Vault folder");
        if (kind < 0) return;
        var source = kind == 0 ? await dialogs.OpenBackupAsync("Select vault backup") : await dialogs.PickFolderAsync("Select the vault folder (contains vault.json)");
        if (source is null) return;
        try
        {
            await Task.Run(() => MaFileVault.Restore(source, VaultDir));
            onRestored();
        }
        catch (SteamGuardException ex) { Error = ex.Message; }
        catch (IOException ex) { Error = ex.Message; }
    }

    public string VaultDir { get; } = vaultDir;
    public bool IsNew { get; } = !MaFileVault.Exists(vaultDir);
    public bool IsExisting => !IsNew;

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
}
