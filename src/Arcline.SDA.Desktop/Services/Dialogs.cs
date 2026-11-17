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

    public async Task<bool> EditSettingsAsync(VaultSettings s)
    {
        var periodic = new CheckBox { Content = "Check for confirmations periodically", IsChecked = s.PeriodicChecking };
        var interval = new NumericUpDown { Minimum = 15, Maximum = 3600, Increment = 15, Value = s.EffectiveIntervalSeconds, FormatString = "0", Width = 160 };
        var all = new CheckBox { Content = "Check all accounts (not only the selected one)", IsChecked = s.CheckAllAccounts };
        var market = new CheckBox { Content = "Auto-confirm market listings (risky)", IsChecked = s.AutoConfirmMarket };
        var trades = new CheckBox { Content = "Auto-confirm trades (risky: confirms ANY trade, including scams)", IsChecked = s.AutoConfirmTrades };
        var tray = new CheckBox { Content = "Closing the window hides it to the tray", IsChecked = s.MinimizeToTray };
        var ok = await ShowAsync<bool>("Settings", "", (p, _) =>
        {
            p.Children.Add(periodic);
            p.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Interval (seconds):", VerticalAlignment = VerticalAlignment.Center }, interval } });
            p.Children.Add(all);
            p.Children.Add(market);
            p.Children.Add(trades);
            p.Children.Add(tray);
            return periodic;
        }, [("Save", true, true), ("Cancel", false, false)]);
        if (!ok) return false;

        var wantsAuto = (market.IsChecked == true && !s.AutoConfirmMarket) || (trades.IsChecked == true && !s.AutoConfirmTrades);
        if (wantsAuto && !await ConfirmAsync("Warning", "Auto-confirmation accepts confirmations without asking you. Anyone who gets a session on your account can then move items freely. Continue?", "Enable", "Cancel"))
        {
            market.IsChecked = s.AutoConfirmMarket;
            trades.IsChecked = s.AutoConfirmTrades;
        }
        s.PeriodicChecking = periodic.IsChecked == true;
        s.PeriodicCheckingIntervalSeconds = (int)(interval.Value ?? 60);
        s.CheckAllAccounts = all.IsChecked == true;
        s.AutoConfirmMarket = market.IsChecked == true;
        s.AutoConfirmTrades = trades.IsChecked == true;
        s.MinimizeToTray = tray.IsChecked == true;
        return true;
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
