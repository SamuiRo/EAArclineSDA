using Arcline.SDA.Desktop.ViewModels;
using Arcline.SDA.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

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
            var dialogs = new Services.Dialogs(() => _window);
            InstallErrorHandlers(dialogs);
            var vm = new MainWindowViewModel(dialogs);
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainWindowViewModel.Current)) UpdateTray(vm); };
            _window.DataContext = vm;
            _window.Closed += (_, _) => desktop.Shutdown();
            // Started at login: live in the tray; the window appears when the tray icon is clicked.
            if (!Program.StartMinimized) desktop.MainWindow = _window;
            UpdateTray(vm);
            StartInstanceServer();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void UpdateTray(MainWindowViewModel vm)
    {
        var icon = TrayIcon.GetIcons(this)?.FirstOrDefault();
        if (icon is null) return;
        icon.ToolTipText = vm.Current is UnlockViewModel ? "Arcline SDA (locked): click to unlock" : "Arcline SDA";
    }

    /// <summary>Lets a second launch (e.g. a double-click while running in the tray) bring this window to the front.</summary>
    private void StartInstanceServer() => Task.Run(async () =>
    {
        while (true)
        {
            try
            {
                using var server = new System.IO.Pipes.NamedPipeServerStream(Program.InstancePipeName, System.IO.Pipes.PipeDirection.In, 1,
                    System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync().ConfigureAwait(false);
                server.ReadByte();
                Dispatcher.UIThread.Post(ShowWindow);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(2000).ConfigureAwait(false);
            }
        }
    });

    /// <summary>
    /// Last line of defence: an error nobody caught is logged and shown instead of closing the app
    /// (closing mid-way through linking an authenticator is the worst possible moment).
    /// </summary>
    private static void InstallErrorHandlers(Services.Dialogs dialogs)
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Services.CrashLog.Write("ui", e.Exception);
            var message = e.Exception is Arcline.SteamGuard.SteamGuardException sg ? sg.Message
                : $"Unexpected error: {e.Exception.GetType().Name}: {e.Exception.Message}";
            _ = dialogs.AlertAsync("Something went wrong",
                message + $"\n\nThe app keeps running. Details were written to:\n{Services.CrashLog.CurrentFile}");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Services.CrashLog.Write("fatal", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Services.CrashLog.Write("task", e.Exception);
            e.SetObserved();
        };
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
