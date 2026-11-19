using Arcline.SteamGuard.Storage;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Arcline.SDA.Desktop.Services;

/// <summary>Modal dialogs built in code (keeps the view models free of UI types).</summary>
public sealed class Dialogs(Func<Window?> owner)
{
    private WindowNotificationManager? _notifications;

    private Window Owner => owner() ?? throw new InvalidOperationException("No main window.");

    public void Notify(string title, string message, NotificationType type = NotificationType.Information)
    {
        _notifications ??= new WindowNotificationManager(TopLevel.GetTopLevel(Owner)) { Position = NotificationPosition.BottomRight, MaxItems = 3 };
        _notifications.Show(new Notification(title, message, type, TimeSpan.FromSeconds(6)));
    }

    public Task AlertAsync(string title, string message, string? copyable = null) =>
        ShowAsync<bool>(title, message, (_, _) => null, [("OK", true, true)], copyable);

    public async Task<bool> ConfirmAsync(string title, string message, string ok = "OK", string cancel = "Cancel") =>
        await ShowAsync<bool>(title, message, (_, _) => null, [(ok, true, true), (cancel, false, false)]);

    public async Task<int> ChooseAsync(string title, string message, params string[] options)
    {
        var buttons = options.Select((o, i) => (o, i, i == 0)).Append(("Cancel", -1, false)).ToArray();
        return await ShowAsync<int>(title, message, (_, _) => null, buttons);
    }

    public async Task<string?> PromptAsync(string title, string message, bool password = false, string? initial = null, string? watermark = null)
    {
        var box = new TextBox { Text = initial ?? "", PasswordChar = password ? '•' : default, Watermark = watermark, MinWidth = 340 };
        var ok = await ShowAsync<bool>(title, message, (panel, _) => { panel.Children.Add(box); return box; }, [("OK", true, true), ("Cancel", false, false)]);
        return ok ? box.Text ?? "" : null;
    }

    /// <summary>Asks for a new secret twice. Returns null on cancel.</summary>
    public async Task<string?> NewPassphraseAsync(string title, string message)
    {
        while (true)
        {
            var a = new TextBox { PasswordChar = '•', Watermark = $"At least {MaFileVault.MinPassphraseLength} characters", MinWidth = 340 };
            var b = new TextBox { PasswordChar = '•', Watermark = "Repeat", MinWidth = 340 };
            var ok = await ShowAsync<bool>(title, message, (p, _) => { p.Children.Add(a); p.Children.Add(b); return a; }, [("OK", true, true), ("Cancel", false, false)]);
            if (!ok) return null;
            if (a.Text != b.Text) { await AlertAsync(title, "The passphrases do not match."); continue; }
            if ((a.Text ?? "").Length < MaFileVault.MinPassphraseLength) { await AlertAsync(title, $"Use at least {MaFileVault.MinPassphraseLength} characters."); continue; }
            return a.Text;
        }
    }

    public async Task<(string User, string Password)?> CredentialsAsync(string title, string message, string? username = null)
    {
        var user = new TextBox { Text = username ?? "", Watermark = "Steam username", IsEnabled = username is null, MinWidth = 340 };
        var pass = new TextBox { PasswordChar = '•', Watermark = "Password", MinWidth = 340 };
        var ok = await ShowAsync<bool>(title, message, (p, _) => { p.Children.Add(user); p.Children.Add(pass); return username is null ? user : pass; },
            [("Log in", true, true), ("Cancel", false, false)]);
        if (!ok || string.IsNullOrWhiteSpace(user.Text) || string.IsNullOrEmpty(pass.Text)) return null;
        return (user.Text.Trim(), pass.Text);
    }

    /// <summary>App-wide options edited in the Settings dialog. Fields not stored in vault.json come from the OS.</summary>
    public sealed record AppOptions(bool StartWithOs, bool StartWithOsSupported, bool AutoUnlock, string? AutoUnlockUnavailable);

    public async Task<AppOptions?> EditSettingsAsync(VaultSettings s, AppOptions current, bool anyAutoConfirm)
    {
        var periodic = new CheckBox { Content = "Check for confirmations periodically", IsChecked = s.PeriodicChecking };
        var interval = new NumericUpDown { Minimum = 15, Maximum = 3600, Increment = 15, Value = s.EffectiveIntervalSeconds, FormatString = "0", Width = 160 };
        var all = new CheckBox { Content = "Notify about all accounts (not only the selected one)", IsChecked = s.CheckAllAccounts };
        var tray = new CheckBox { Content = "Minimizing or closing the window keeps the app running in the tray", IsChecked = s.MinimizeToTray };
        var autostart = new CheckBox
        {
            Content = OperatingSystem.IsWindows() ? "Start with Windows (in the tray)" : "Start when I log in (in the tray)",
            IsChecked = current.StartWithOs, IsEnabled = current.StartWithOsSupported,
        };
        var autoUnlock = new CheckBox
        {
            Content = "Unlock the vault automatically on this PC",
            IsChecked = current.AutoUnlock, IsEnabled = current.AutoUnlockUnavailable is null,
        };
        static TextBlock Note(string text) => new()
        {
            Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 470, Opacity = 0.7, FontSize = 12, Margin = new Thickness(28, -4, 0, 4),
        };
        static TextBlock Header(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) };

        var ok = await ShowAsync<bool>("Settings", "", (p, _) =>
        {
            p.Children.Add(Header("Background checking"));
            p.Children.Add(periodic);
            p.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(28, 0, 0, 0), Children = { new TextBlock { Text = "Every (seconds):", VerticalAlignment = VerticalAlignment.Center }, interval } });
            p.Children.Add(all);
            p.Children.Add(Note("Auto-confirm is set per account: select an account, then Account > Auto-confirm. Accounts with auto-confirm are always checked."));

            p.Children.Add(Header("Tray and startup"));
            p.Children.Add(tray);
            p.Children.Add(autostart);
            p.Children.Add(autoUnlock);
            p.Children.Add(Note(current.AutoUnlockUnavailable ??
                "Stores the vault passphrase in the OS's protected store (Windows DPAPI / the Linux keyring), so auto-confirm can run right after startup. " +
                "Any program running as your OS user could then open the vault on this PC. Backups and exported files still need the passphrase."));
            return periodic;
        }, [("Save", true, true), ("Cancel", false, false)]);
        if (!ok) return null;

        s.PeriodicCheckingIntervalSeconds = (int)(interval.Value ?? 60);
        s.CheckAllAccounts = all.IsChecked == true;
        // Auto-confirm does nothing without background checking, so keep it on while any account uses it.
        s.PeriodicChecking = periodic.IsChecked == true || anyAutoConfirm;
        s.MinimizeToTray = tray.IsChecked == true || autostart.IsChecked == true; // starting in the tray implies living in the tray
        return current with { StartWithOs = autostart.IsChecked == true, AutoUnlock = autoUnlock.IsChecked == true };
    }

    /// <summary>Per-account auto-confirm switches. Returns the new rules, or null on cancel.</summary>
    public async Task<AutoConfirmRules?> EditAutoConfirmAsync(string account, AutoConfirmRules current)
    {
        var trades = new CheckBox { Content = "Trades (accepts ANY trade offer, including scams)", IsChecked = current.Trades };
        var market = new CheckBox { Content = "Market listings (selling items)", IsChecked = current.Market };
        var purchases = new CheckBox { Content = "Purchases", IsChecked = current.Purchases };
        var ok = await ShowAsync<bool>($"Auto-confirm - {account}",
            $"What should {account} confirm automatically? Everything unchecked stays manual.", (p, _) =>
            {
                p.Children.Add(trades);
                p.Children.Add(market);
                p.Children.Add(purchases);
                p.Children.Add(new TextBlock
                {
                    Text = "API keys, phone or recovery changes and any unknown type are never auto-confirmed. " +
                           "Works while the app is running (window or tray) and unlocked; background checking is turned on automatically.",
                    TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Opacity = 0.7, FontSize = 12,
                });
                return trades;
            }, [("Save", true, true), ("Cancel", false, false)]);
        if (!ok) return null;

        var next = new AutoConfirmRules { Trades = trades.IsChecked == true, Market = market.IsChecked == true, Purchases = purchases.IsChecked == true };
        var newlyEnabled = new List<string>();
        if (next.Trades && !current.Trades) newlyEnabled.Add("trades");
        if (next.Market && !current.Market) newlyEnabled.Add("market listings");
        if (next.Purchases && !current.Purchases) newlyEnabled.Add("purchases");
        if (newlyEnabled.Count > 0 && !await ConfirmAsync("Warning",
                $"Auto-confirm {string.Join(", ", newlyEnabled)} for {account}?\n\nThese will be accepted without asking you. If someone else gets into your Steam session, they can move items and money freely.",
                "Enable", "Cancel"))
            return null;
        return next;
    }

    public async Task<string?> OpenFileAsync(string title, params string[] patterns)
    {
        var files = await Owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("maFile") { Patterns = patterns }, FilePickerFileTypes.All],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> OpenBackupAsync(string title)
    {
        var files = await Owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Vault backup") { Patterns = ["*.zip"] }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName, string extension = "maFile")
    {
        var file = await Owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await Owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private async Task<T> ShowAsync<T>(string title, string message, Func<StackPanel, Window, Control?> addContent,
        (string Text, T Result, bool IsDefault)[] buttons, string? copyable = null)
    {
        var win = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            MinWidth = 380,
            MaxWidth = 640,
        };
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        if (!string.IsNullOrEmpty(message))
            panel.Children.Add(new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 600 });
        if (copyable is not null)
            panel.Children.Add(new TextBox { Text = copyable, IsReadOnly = true, FontFamily = new FontFamily("Consolas,Menlo,monospace"), FontSize = 18 });
        var focus = addContent(panel, win);

        // Closing with the window's X counts as the last button (always "Cancel" when there is one).
        var result = buttons[^1].Result;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (text, value, isDefault) in buttons)
        {
            var b = new Button { Content = text, IsDefault = isDefault, IsCancel = !isDefault && text == "Cancel", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (isDefault) b.Classes.Add("accent");
            b.Click += (_, _) => { result = value; win.Close(); };
            row.Children.Add(b);
        }
        panel.Children.Add(row);
        win.Content = panel;
        win.Opened += (_, _) => focus?.Focus();

        if (Owner.IsVisible) await win.ShowDialog(Owner);
        else
        {
            var tcs = new TaskCompletionSource();
            win.Closed += (_, _) => tcs.TrySetResult();
            win.Show();
            await tcs.Task;
        }
        return result;
    }
}
