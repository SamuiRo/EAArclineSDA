using System.Collections.ObjectModel;
using Arcline.SDA.Desktop.Services;
using Arcline.SteamGuard;
using Arcline.SteamGuard.Crypto;
using Arcline.SteamGuard.Legacy;
using Arcline.SteamGuard.Models;
using Arcline.SteamGuard.Steam;
using Arcline.SteamGuard.Storage;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arcline.SDA.Desktop.ViewModels;

public sealed partial class AccountsViewModel : ObservableObject, IDisposable
{
    private readonly Dialogs _dialogs;
    private readonly Action _lock;
    private readonly Action<string> _setTitle;
    private readonly Dictionary<ulong, SteamGuardAccount> _cache = [];
    private readonly DispatcherTimer _codeTimer;
    private readonly DispatcherTimer _pollTimer;
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly HashSet<ulong> _seenConfirmations = [];
    private bool _disposed;

    public AccountsViewModel(MaFileVault vault, Dialogs dialogs, Action lockAction, Action<string> setTitle, SteamGuardClient? client = null)
    {
        Vault = vault;
        _dialogs = dialogs;
        _lock = lockAction;
        _setTitle = setTitle;
        Client = client ?? new SteamGuardClient(vault);
        _codeTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateCode());
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(60) };
        _pollTimer.Tick += async (_, _) => await PollAsync();
    }

    public MaFileVault Vault { get; }
    public SteamGuardClient Client { get; }

    public ObservableCollection<AccountItem> Accounts { get; } = [];
    public ObservableCollection<AccountItem> FilteredAccounts { get; } = [];
    public ObservableCollection<ConfirmationItem> Confirmations { get; } = [];

    [ObservableProperty] private string _search = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasSelection))] private AccountItem? _selected;
    [ObservableProperty] private string _code = "-----";
    [ObservableProperty] private int _secondsLeft;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _loadingConfirmations;
    [ObservableProperty] private string? _confirmationsError;
    [ObservableProperty] private bool _timeSynced;

    public bool HasSelection => Selected is not null;
    public bool HasNoAccounts => Accounts.Count == 0;
    public string VaultPath => Vault.Directory;

    // ------------------------------------------------------------------ lifecycle

    public void Start()
    {
        ReloadAccounts();
        ApplySettings();
        _codeTimer.Start();
        _ = SyncTimeAsync();
        var stray = Vault.FindPlaintextFiles();
        if (stray.Count > 0) _ = OfferEncryptStrayAsync(stray);
    }

    private async Task SyncTimeAsync()
    {
        Status = "Syncing time with Steam...";
        await Client.Time.SyncAsync();
        TimeSynced = Client.Time.IsSynced;
        Status = TimeSynced ? "" : "Could not reach Steam to sync time; using the local clock.";
    }

    private void ApplySettings()
    {
        _pollTimer.Interval = TimeSpan.FromSeconds(Vault.Settings.EffectiveIntervalSeconds);
        _pollTimer.IsEnabled = Vault.Settings.PeriodicChecking;
    }

    private void ReloadAccounts(ulong? select = null)
    {
        var prev = select ?? Selected?.SteamId;
        _shown = null;
        _cache.Clear();
        Accounts.Clear();
        foreach (var e in Vault.Entries)
        {
            try
            {
                _cache[e.SteamId] = Vault.Load(e.SteamId);
                Accounts.Add(new AccountItem(e.SteamId, e.DisplayName));
            }
            catch (SteamGuardException ex)
            {
                Status = $"Cannot open {e.DisplayName}: {ex.Message}";
            }
        }
        ApplyFilter();
        Selected = FilteredAccounts.FirstOrDefault(a => a.SteamId == prev) ?? FilteredAccounts.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoAccounts));
    }

    partial void OnSearchChanged(string value) => ApplyFilter();

    private bool _filtering;
    private AccountItem? _shown;

    private void ApplyFilter()
    {
        var keep = Selected;
        _filtering = true; // the ListBox nulls SelectedItem while its source is rebuilt; ignore that
        try
        {
            FilteredAccounts.Clear();
            var q = Search.Trim();
            foreach (var a in Accounts.Where(a => q.Length == 0 || a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || a.SteamId.ToString().Contains(q, StringComparison.Ordinal)))
                FilteredAccounts.Add(a);
        }
        finally { _filtering = false; }
        Selected = keep is not null && FilteredAccounts.Contains(keep) ? keep : FilteredAccounts.FirstOrDefault();
    }

    partial void OnSelectedChanged(AccountItem? value)
    {
        if (_filtering || ReferenceEquals(value, _shown)) return;
        _shown = value;
        UpdateCode();
        _setTitle(value is null ? "Arcline SDA" : $"Arcline SDA - {value.Name}");
        // In "all accounts" mode the list does not depend on the selected account.
        if (ShowAllAccounts) return;
        _allConfirmations.Clear();
        RebuildConfirmations();
        ConfirmationsError = null;
        if (value is not null) _ = RefreshConfirmationsAsync();
    }

    private SteamGuardAccount? Current => Selected is { } s && _cache.TryGetValue(s.SteamId, out var a) ? a : null;

    private void UpdateCode()
    {
        if (Current is not { SharedSecret: { Length: > 0 } secret }) { Code = "-----"; SecondsLeft = 0; return; }
        var t = Client.Time.Now;
        Code = SteamGuardCode.Generate(secret, t);
        SecondsLeft = SteamGuardCode.SecondsRemaining(t);
        // Once per window: re-sync if the offset is stale (network only every 30 min, or 60 s after a failure).
        if (SecondsLeft == SteamGuardCode.PeriodSeconds) _ = Client.Time.NowAsync();
    }

    // ------------------------------------------------------------------ code / clipboard

    [RelayCommand]
    private async Task CopyCodeAsync()
    {
        if (Current is null) return;
        var clipboard = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(Code);
        Status = "Code copied.";
    }

    // ------------------------------------------------------------------ confirmations

    // All loaded confirmations; Confirmations is the filtered view the UI shows.
    private readonly List<ConfirmationItem> _allConfirmations = [];
    private int _loadVersion;
    private bool _bulkSelecting;

    [ObservableProperty] private bool _showAllAccounts;
    [ObservableProperty] private ConfirmationFilter _filter = ConfirmationFilter.All;

    public IReadOnlyList<ConfirmationFilter> Filters { get; } = Enum.GetValues<ConfirmationFilter>();

    public int SelectedCount => Confirmations.Count(c => c.IsSelected);
    public bool HasSelectedConfirmations => SelectedCount > 0;
    public string AcceptSelectedText => $"Accept selected ({SelectedCount})";
    public string DenySelectedText => $"Deny selected ({SelectedCount})";
    public string ConfirmationsHeader => Confirmations.Count == _allConfirmations.Count
        ? $"Confirmations ({Confirmations.Count})"
        : $"Confirmations ({Confirmations.Count} of {_allConfirmations.Count})";

    /// <summary>Header checkbox: checked = all visible selected, unchecked = none, null = some.</summary>
    public bool? AllSelected
    {
        get => Confirmations.Count == 0 || SelectedCount == 0 ? false : SelectedCount == Confirmations.Count ? true : null;
        set => SetAllSelected(value != false);
    }

    partial void OnShowAllAccountsChanged(bool value)
    {
        _allConfirmations.Clear();
        RebuildConfirmations();
        _ = RefreshConfirmationsAsync();
    }

    partial void OnFilterChanged(ConfirmationFilter value) => RebuildConfirmations();

    private void RebuildConfirmations()
    {
        Confirmations.Clear();
        foreach (var c in _allConfirmations.Where(c => c.Matches(Filter))) Confirmations.Add(c);
        // Hidden items must not be acted on by "selected" actions.
        foreach (var hidden in _allConfirmations.Where(c => !c.Matches(Filter))) hidden.IsSelected = false;
        NotifySelection();
    }

    private void NotifySelection()
    {
        if (_bulkSelecting) return;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelectedConfirmations));
        OnPropertyChanged(nameof(AcceptSelectedText));
        OnPropertyChanged(nameof(DenySelectedText));
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(ConfirmationsHeader));
        AcceptSelectedCommand.NotifyCanExecuteChanged();
        DenySelectedCommand.NotifyCanExecuteChanged();
    }

    private void SetAllSelected(bool selected)
    {
        _bulkSelecting = true;
        try { foreach (var c in Confirmations) c.IsSelected = selected; }
        finally { _bulkSelecting = false; }
        NotifySelection();
    }

    [RelayCommand]
    private void SelectAll() => SetAllSelected(true);

    [RelayCommand]
    private void SelectNone() => SetAllSelected(false);

    private ConfirmationItem Track(ConfirmationItem item)
    {
        item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ConfirmationItem.IsSelected)) NotifySelection(); };
        return item;
    }

    [RelayCommand]
    private async Task RefreshConfirmationsAsync()
    {
        var targets = ShowAllAccounts ? Accounts.ToList() : Selected is null ? [] : [Selected];
        if (targets.Count == 0) return;
        var version = ++_loadVersion;
        var keepSelected = _allConfirmations.Where(c => c.IsSelected).Select(c => c.Id).ToHashSet();
        LoadingConfirmations = true;
        ConfirmationsError = null;
        var loaded = new List<ConfirmationItem>();
        var problems = new List<string>();
        try
        {
            foreach (var item in targets)
            {
                if (!_cache.TryGetValue(item.SteamId, out var acc)) continue;
                try
                {
                    var list = await Client.Confirmations.GetAsync(acc);
                    item.Pending = list.Count;
                    item.NeedsLogin = false;
                    loaded.AddRange(list.Select(c => Track(new ConfirmationItem(c, item, ShowAllAccounts) { IsSelected = keepSelected.Contains(c.Id) })));
                }
                catch (SessionExpiredException)
                {
                    item.NeedsLogin = true;
                    problems.Add(ShowAllAccounts ? $"{item.Name}: session expired, use \"Log in again\"." : "Session expired. Use \"Log in again\".");
                }
                catch (SteamGuardException ex) { problems.Add(ShowAllAccounts ? $"{item.Name}: {ex.Message}" : ex.Message); }
                if (version != _loadVersion) return; // a newer refresh (or account switch) superseded this one
                if (targets.Count > 1) await Task.Delay(300);
            }
            _allConfirmations.Clear();
            _allConfirmations.AddRange(loaded.OrderByDescending(c => c.Confirmation.CreationTime));
            RebuildConfirmations();
            if (problems.Count > 0) ConfirmationsError = string.Join(Environment.NewLine, problems);
            else if (loaded.Count == 0) ConfirmationsError = "Nothing to confirm.";
        }
        finally { if (version == _loadVersion) LoadingConfirmations = false; }
    }

    [RelayCommand]
    private Task AcceptAsync(ConfirmationItem item) => RespondAsync([item], true);

    [RelayCommand]
    private Task DenyAsync(ConfirmationItem item) => RespondAsync([item], false);

    [RelayCommand(CanExecute = nameof(HasSelectedConfirmations))]
    private Task AcceptSelectedAsync() => RespondAsync(Confirmations.Where(c => c.IsSelected).ToList(), true);

    [RelayCommand(CanExecute = nameof(HasSelectedConfirmations))]
    private async Task DenySelectedAsync()
    {
        var items = Confirmations.Where(c => c.IsSelected).ToList();
        if (items.Count > 1 && !await _dialogs.ConfirmAsync("Deny selected", $"Deny {items.Count} confirmations? Denied trades are cancelled.", "Deny")) return;
        await RespondAsync(items, false);
    }

    /// <summary>Responds per account (one batched Steam request per account), then updates the list in place.</summary>
    private async Task RespondAsync(IReadOnlyList<ConfirmationItem> items, bool accept)
    {
        if (items.Count == 0) return;
        foreach (var i in items) { i.Busy = true; i.Error = null; }
        int ok = 0, failed = 0;
        try
        {
            foreach (var group in items.GroupBy(i => i.Account))
            {
                if (!_cache.TryGetValue(group.Key.SteamId, out var acc)) continue;
                try
                {
                    var results = await Client.Confirmations.RespondAsync(acc, group.Select(i => i.Confirmation).ToList(), accept);
                    foreach (var r in results)
                    {
                        var item = group.First(i => i.Id == r.ConfirmationId);
                        if (r.Success)
                        {
                            ok++;
                            _allConfirmations.Remove(item);
                            Confirmations.Remove(item);
                        }
                        else
                        {
                            failed++;
                            item.Error = r.Message;
                        }
                    }
                    group.Key.Pending = _allConfirmations.Count(c => c.Account == group.Key);
                }
                catch (SessionExpiredException)
                {
                    group.Key.NeedsLogin = true;
                    failed += group.Count();
                    foreach (var i in group) i.Error = "Session expired. Use \"Log in again\" for this account.";
                }
                catch (SteamGuardException ex)
                {
                    failed += group.Count();
                    foreach (var i in group) i.Error = ex.Message;
                }
            }
            Status = failed == 0 ? $"{(accept ? "Accepted" : "Denied")} {ok}." : $"{(accept ? "Accepted" : "Denied")} {ok}, {failed} failed (see the cards).";
            if (_allConfirmations.Count == 0) ConfirmationsError = "Nothing to confirm.";
        }
        finally
        {
            foreach (var i in items) i.Busy = false;
            NotifySelection();
        }
    }

    [RelayCommand]
    private async Task AcknowledgeTradeProtectionAsync()
    {
        if (Current is not { } acc) return;
        try
        {
            await Client.Confirmations.AcknowledgeTradeProtectionAsync(acc);
            Status = "Trade protection notice acknowledged. Trade confirmations should work now.";
        }
        catch (SteamGuardException ex) { await _dialogs.AlertAsync("Trade protection", ex.Message); }
    }

    // ------------------------------------------------------------------ background polling

    private async Task PollAsync()
    {
        if (_disposed || !await _pollGate.WaitAsync(0)) return;
        try
        {
            var targets = Vault.Settings.CheckAllAccounts ? Accounts.ToList() : Selected is null ? [] : [Selected];
            var newOnes = new List<string>();
            var refreshVisible = false;
            foreach (var item in targets)
            {
                if (!_cache.TryGetValue(item.SteamId, out var acc) || item.NeedsLogin) continue;
                try
                {
                    var list = await Client.Confirmations.GetAsync(acc);
                    var auto = list.Where(c => (c.Type == ConfirmationType.MarketListing && Vault.Settings.AutoConfirmMarket)
                                            || (c.Type == ConfirmationType.Trade && Vault.Settings.AutoConfirmTrades)).ToList();
                    if (auto.Count > 0)
                    {
                        var res = await Client.Confirmations.AcceptAsync(acc, auto);
                        var ok = res.Count(r => r.Success);
                        if (ok > 0) _dialogs.Notify(item.Name, $"Auto-confirmed {ok} confirmation(s).", NotificationType.Success);
                    }
                    var manual = list.Except(auto).ToList();
                    item.Pending = manual.Count;
                    var fresh = manual.Where(c => _seenConfirmations.Add(c.Id)).ToList();
                    if (fresh.Count > 0) newOnes.Add($"{item.Name}: {fresh.Count} new");
                    if (fresh.Count > 0 && (ShowAllAccounts || item == Selected)) refreshVisible = true;
                }
                catch (SessionExpiredException)
                {
                    item.NeedsLogin = true;
                    _dialogs.Notify(item.Name, "Session expired. Select the account and use \"Log in again\".", NotificationType.Warning);
                }
                catch (SteamGuardException) { /* transient; next tick retries */ }
                if (targets.Count > 1) await Task.Delay(750); // be gentle with Steam when checking many accounts
            }
            if (newOnes.Count > 0) _dialogs.Notify("Confirmations waiting", string.Join(Environment.NewLine, newOnes));
            if (refreshVisible) await RefreshConfirmationsAsync();
        }
        finally { _pollGate.Release(); }
    }

    // ------------------------------------------------------------------ account management

    [RelayCommand]
    private async Task LoginAgainAsync()
    {
        if (Current is not { } acc || Selected is not { } item) return;
        var creds = await _dialogs.CredentialsAsync("Log in again", "Your Steam session is refreshed and saved encrypted. Codes are generated automatically from this maFile.", acc.AccountName);
        if (creds is null) return;
        Status = "Logging in...";
        try
        {
            await Client.ReloginAsync(acc, creds.Value.Password, new GuiLoginPrompts(_dialogs));
            item.NeedsLogin = false;
            Status = "Session refreshed.";
            await RefreshConfirmationsAsync();
        }
        catch (OperationCanceledException) { Status = "Login cancelled."; }
        catch (SteamGuardException ex) { Status = ""; await _dialogs.AlertAsync("Login failed", ex.Message); }
    }

    [RelayCommand]
    private async Task ImportMaFileAsync()
    {
        var path = await _dialogs.OpenFileAsync("Import maFile", "*.maFile", "*.json");
        if (path is null) return;
        try
        {
            string? pass = null;
            var text = await File.ReadAllTextAsync(path);
            var encrypted = MaFileEnvelope.LooksLikeEnvelope(text);
            if (encrypted)
            {
                pass = await _dialogs.PromptAsync("Import maFile", "This maFile is encrypted. Enter its passphrase:", password: true);
                if (pass is null) return;
            }
            var acc = await Task.Run(() => MaFileVault.ReadExternalFile(path, pass));

            if (acc.SteamId == 0 || acc.Session?.IsRefreshTokenExpired() != false)
            {
                var creds = await _dialogs.CredentialsAsync("Import maFile", "This maFile has no valid session. Log in to finish the import (codes are generated from the file).", acc.AccountName);
                if (creds is null) return;
                var session = await Client.Login.LoginAsync(creds.Value.User, creds.Value.Password, new GuiLoginPrompts(_dialogs), acc);
                acc.Session = session;
                acc.AccountName ??= creds.Value.User;
            }

            if (Vault.Entries.Any(e => e.SteamId == acc.SteamId) &&
                !await _dialogs.ConfirmAsync("Import maFile", $"{acc.DisplayName} is already in the vault. Replace it?", "Replace")) return;
            Vault.Save(acc);

            if (!encrypted && await _dialogs.ConfirmAsync("Import maFile",
                    $"Imported and encrypted {acc.DisplayName}.\n\nThe source file is PLAINTEXT. Securely delete it now?\n{path}", "Delete it", "Keep"))
                SecureFile.Shred(path);
            ReloadAccounts(acc.SteamId);
        }
        catch (WrongPassphraseException) { await _dialogs.AlertAsync("Import maFile", "Wrong passphrase for this file."); }
        catch (OperationCanceledException) { }
        catch (SteamGuardException ex) { await _dialogs.AlertAsync("Import maFile", ex.Message); }
    }

    [RelayCommand]
    private async Task ImportSdaFolderAsync()
    {
        var folder = await _dialogs.PickFolderAsync("Select your old Steam Desktop Authenticator folder (or its maFiles folder)");
        if (folder is null) return;
        try
        {
            string? passKey = null;
            if (LegacySdaImporter.IsEncrypted(LegacySdaImporter.ResolveMaFilesDir(folder)))
            {
                passKey = await _dialogs.PromptAsync("Import SDA", "These maFiles are encrypted by SDA. Enter the old SDA passkey:", password: true);
                if (passKey is null) return;
            }
            var res = await Task.Run(() => Vault.ImportLegacySda(folder, passKey));
            ReloadAccounts();
            var msg = $"Imported {res.Accounts.Count} account(s) into the encrypted vault.";
            if (res.Errors.Count > 0) msg += "\n\nProblems:\n" + string.Join("\n", res.Errors);
            msg += "\n\nThe old SDA folder was not modified. Once you have verified the codes here, delete it (its files may be unencrypted).";
            await _dialogs.AlertAsync("Import SDA", msg);
        }
        catch (SteamGuardException ex) { await _dialogs.AlertAsync("Import SDA", ex.Message); }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Current is not { } acc) return;
        var choice = await _dialogs.ChooseAsync("Export maFile",
            $"Export {acc.DisplayName} for use in another program or PC.\n\n" +
            "Encrypted: standalone file protected by a separate transfer passphrase. Decrypt it with 'arcline-sda decrypt', the .NET library, or tools/mafile_decrypt.py.\n\n" +
            "Plaintext: SDA-compatible JSON that any tool can read. Anyone who gets it controls the account.",
            "Encrypted (recommended)", "Plaintext");
        if (choice < 0) return;
        var path = await _dialogs.SaveFileAsync("Export maFile", $"{acc.AccountName ?? acc.SteamId.ToString()}{(choice == 0 ? ".enc" : "")}.maFile");
        if (path is null) return;
        try
        {
            if (choice == 0)
            {
                var tp = await _dialogs.NewPassphraseAsync("Transfer passphrase", "Passphrase for the exported file (can differ from the vault passphrase):");
                if (tp is null) return;
                await Task.Run(() => Vault.Export(acc.SteamId, path, ExportMode.Encrypted, tp));
            }
            else
            {
                if (!await _dialogs.ConfirmAsync("Plaintext export", "The file will contain your secrets unencrypted. Delete it right after importing it elsewhere.", "Export plaintext")) return;
                Vault.Export(acc.SteamId, path, ExportMode.Plaintext);
            }
            Status = $"Exported to {path}";
        }
        catch (SteamGuardException ex) { await _dialogs.AlertAsync("Export", ex.Message); }
    }

    [RelayCommand]
    private async Task AddAuthenticatorAsync()
    {
        var creds = await _dialogs.CredentialsAsync("Add authenticator", "Log in to the Steam account that should use this app as its authenticator.");
        if (creds is null) return;
        try
        {
            Status = "Logging in...";
            var session = await Client.Login.LoginAsync(creds.Value.User, creds.Value.Password, new GuiLoginPrompts(_dialogs));
            var linker = Client.CreateLinker(session);
            Status = "Adding authenticator...";
            while (true)
            {
                var r = await linker.AddAsync();
                if (r == AuthenticatorLinker.LinkResult.AwaitingFinalization) break;
                switch (r)
                {
                    case AuthenticatorLinker.LinkResult.MustProvidePhoneNumber:
                        linker.PhoneNumber = (await _dialogs.PromptAsync("Phone number", "This account has no phone number. Enter one in international format:", watermark: "+380..."))?.Trim();
                        if (string.IsNullOrEmpty(linker.PhoneNumber)) { Status = "Cancelled."; return; }
                        break;
                    case AuthenticatorLinker.LinkResult.MustConfirmEmail:
                        if (!await _dialogs.ConfirmAsync("Confirm e-mail", $"Click the link Steam sent to {linker.ConfirmationEmailAddress ?? "your e-mail"}, then press Continue.", "Continue")) { Status = "Cancelled."; return; }
                        break;
                    case AuthenticatorLinker.LinkResult.FailureAddingPhone:
                        await _dialogs.AlertAsync("Phone number", "Steam rejected that phone number. Try another one.");
                        linker.PhoneNumber = null;
                        break;
                    case AuthenticatorLinker.LinkResult.AuthenticatorPresent:
                        await _dialogs.AlertAsync("Add authenticator", "This account already has an authenticator. Remove it first, or import its maFile instead.");
                        Status = "";
                        return;
                    default:
                        await _dialogs.AlertAsync("Add authenticator", "Steam refused to add the authenticator. " + linker.LastError);
                        Status = "";
                        return;
                }
            }

            var acc = linker.LinkedAccount!;
            acc.AccountName ??= creds.Value.User;
            Vault.Save(acc); // persist the secrets (encrypted) before finalizing
            await _dialogs.AlertAsync("Revocation code",
                "Write this revocation code down and keep it safe. It is the only way to remove the authenticator if you lose this vault.", acc.RevocationCode);

            while (true)
            {
                var code = await _dialogs.PromptAsync("Activation code", linker.ConfirmType == 1 ? "Enter the SMS code Steam sent to your phone:" : "Enter the code Steam e-mailed to you:");
                if (string.IsNullOrWhiteSpace(code)) { Status = "Not finalized. The encrypted maFile stays in the vault; the authenticator is NOT active yet."; ReloadAccounts(acc.SteamId); return; }
                var rev = await _dialogs.PromptAsync("Revocation code", "Type the revocation code to prove you saved it:");
                if (!string.Equals(rev?.Trim(), acc.RevocationCode, StringComparison.OrdinalIgnoreCase))
                {
                    await _dialogs.AlertAsync("Revocation code", "That does not match. Try again.", acc.RevocationCode);
                    continue;
                }
                var f = await linker.FinalizeAsync(code);
                if (f == AuthenticatorLinker.FinalizeResult.Success) break;
                if (f == AuthenticatorLinker.FinalizeResult.BadCode) { await _dialogs.AlertAsync("Activation code", "Wrong code, try again."); continue; }
                await _dialogs.AlertAsync("Add authenticator", $"Finalizing failed ({f}). Keep your revocation code.", acc.RevocationCode);
                return;
            }
            Vault.Save(acc);
            ReloadAccounts(acc.SteamId);
            Status = $"Authenticator linked for {acc.AccountName}.";
        }
        catch (OperationCanceledException) { Status = "Cancelled."; }
        catch (SteamGuardException ex) { Status = ""; await _dialogs.AlertAsync("Add authenticator", ex.Message); }
    }

    [RelayCommand]
    private async Task RemoveAccountAsync()
    {
        if (Selected is not { } item) return;
        if (!await _dialogs.ConfirmAsync("Remove from vault",
                $"Remove {item.Name} from this vault?\n\nThis does NOT remove the authenticator from Steam. An encrypted copy is kept in the vault's backups folder.", "Remove")) return;
        Vault.Remove(item.SteamId, keepBackup: true);
        ReloadAccounts();
    }

    [RelayCommand]
    private async Task DeactivateAsync()
    {
        if (Current is not { } acc) return;
        var choice = await _dialogs.ChooseAsync("Remove authenticator",
            $"Remove the Steam Guard mobile authenticator from {acc.DisplayName}?", "Switch to e-mail codes", "Remove Steam Guard completely");
        if (choice < 0) return;
        var code = SteamGuardCode.Generate(acc.SharedSecret!, Client.Time.Now);
        var typed = await _dialogs.PromptAsync("Remove authenticator", $"To confirm, type this code: {code}");
        if (!string.Equals(typed?.Trim(), code, StringComparison.OrdinalIgnoreCase)) { Status = "Codes did not match; nothing was changed."; return; }
        try
        {
            await Client.Sessions.EnsureFreshAsync(acc);
            if (!await AuthenticatorLinker.RemoveAsync(Client.Http, acc, choice == 1 ? 2 : 1))
            {
                await _dialogs.AlertAsync("Remove authenticator", "Steam refused to remove the authenticator.");
                return;
            }
            Vault.Remove(acc.SteamId, keepBackup: true);
            ReloadAccounts();
            await _dialogs.AlertAsync("Remove authenticator", "Authenticator removed. The encrypted maFile was moved to the vault's backups folder.");
        }
        catch (SteamGuardException ex) { await _dialogs.AlertAsync("Remove authenticator", ex.Message); }
    }

    [RelayCommand]
    private void MoveUp() => Move(-1);

    [RelayCommand]
    private void MoveDown() => Move(1);

    private void Move(int delta)
    {
        if (Selected is not { } s) return;
        var idx = Accounts.IndexOf(s);
        Vault.Move(idx, idx + delta);
        ReloadAccounts(s.SteamId);
    }

    // ------------------------------------------------------------------ vault

    [RelayCommand]
    private async Task SettingsAsync()
    {
        if (!await _dialogs.EditSettingsAsync(Vault.Settings)) return;
        Vault.SaveIndex();
        ApplySettings();
    }

    [RelayCommand]
    private async Task ChangePassphraseAsync()
    {
        var current = await _dialogs.PromptAsync("Change passphrase", "Current vault passphrase:", password: true);
        if (current is null) return;
        if (!await Task.Run(() => Vault.VerifyPassphrase(current))) { await _dialogs.AlertAsync("Change passphrase", "Wrong passphrase."); return; }
        var next = await _dialogs.NewPassphraseAsync("Change passphrase", "New vault passphrase:");
        if (next is null) return;
        await Task.Run(() => Vault.ChangePassphrase(next));
        Status = "Passphrase changed; all maFiles re-encrypted.";
    }

    [RelayCommand]
    private async Task BackupVaultAsync()
    {
        var path = await _dialogs.SaveFileAsync("Back up vault", $"arcline-vault-{DateTime.Now:yyyy-MM-dd}.zip", "zip");
        if (path is null) return;
        try
        {
            await Task.Run(() => Vault.Backup(path));
            await _dialogs.AlertAsync("Back up vault",
                $"Saved {Vault.Entries.Count} account(s) to:\n{path}\n\nThe backup stays encrypted. On the new PC choose \"Restore from backup\" on the first screen and use the same vault passphrase.");
        }
        catch (Exception ex) when (ex is SteamGuardException or IOException or UnauthorizedAccessException)
        {
            await _dialogs.AlertAsync("Back up vault", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ImportVaultAsync()
    {
        var kind = await _dialogs.ChooseAsync("Import from another vault",
            "Merge the accounts of another Arcline vault (e.g. from your other PC) into this one.", "Backup .zip", "Vault folder");
        if (kind < 0) return;
        var source = kind == 0 ? await _dialogs.OpenBackupAsync("Select vault backup") : await _dialogs.PickFolderAsync("Select the other vault folder (contains vault.json)");
        if (source is null) return;
        var pass = await _dialogs.PromptAsync("Import from another vault", "Passphrase of THAT vault:", password: true);
        if (pass is null) return;
        try
        {
            var res = await Task.Run(() => Vault.ImportVault(source, pass, overwrite: false));
            ReloadAccounts();
            var msg = $"Imported {res.Accounts.Count} account(s).";
            if (res.Errors.Count > 0) msg += "\n\nSkipped:\n" + string.Join("\n", res.Errors);
            await _dialogs.AlertAsync("Import from another vault", msg);
        }
        catch (WrongPassphraseException) { await _dialogs.AlertAsync("Import from another vault", "Wrong passphrase for that vault."); }
        catch (SteamGuardException ex) { await _dialogs.AlertAsync("Import from another vault", ex.Message); }
    }

    [RelayCommand]
    private void Lock() => _lock();

    [RelayCommand]
    private static void Quit() => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();

    private async Task OfferEncryptStrayAsync(IReadOnlyList<string> files)
    {
        if (!await _dialogs.ConfirmAsync("Unencrypted maFiles found",
                $"{files.Count} plaintext maFile(s) are in the vault folder. Import them into the vault (encrypted) and securely delete the plaintext copies?", "Encrypt them", "Not now")) return;
        foreach (var f in files)
        {
            try { Vault.Import(f, overwrite: false, shredSource: true); }
            catch (SteamGuardException ex) { Status = $"{Path.GetFileName(f)}: {ex.Message}"; }
        }
        ReloadAccounts();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _codeTimer.Stop();
        _pollTimer.Stop();
        _cache.Clear();
        Vault.Dispose();
    }
}
