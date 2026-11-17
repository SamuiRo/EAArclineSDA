using Arcline.SDA.Desktop.ViewModels;
using Arcline.SDA.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Arcline.SDA.Desktop;

public sealed partial class App : Application
{
    private MainWindow? _window;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _window = new MainWindow();
            _window.DataContext = new MainWindowViewModel(new Services.Dialogs(() => _window));
            _window.Closed += (_, _) => desktop.Shutdown();
            desktop.MainWindow = _window;
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void Tray_Clicked(object? sender, EventArgs e) => ShowWindow();
    private void Tray_Show(object? sender, EventArgs e) => ShowWindow();

    private void Tray_Quit(object? sender, EventArgs e)
    {
        if (_window is not null) _window.AllowClose = true;
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
