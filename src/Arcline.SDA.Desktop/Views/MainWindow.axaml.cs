using Arcline.SDA.Desktop.ViewModels;
using Avalonia.Controls;

namespace Arcline.SDA.Desktop.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>Set by the tray "Quit" item so closing really exits.</summary>
    public bool AllowClose { get; set; }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!AllowClose && DataContext is MainWindowViewModel { MinimizeToTray: true })
        {
            e.Cancel = true;
            Hide();
            return;
        }
        if (DataContext is MainWindowViewModel { Accounts: { } a }) a.Dispose();
        base.OnClosing(e);
    }
}
