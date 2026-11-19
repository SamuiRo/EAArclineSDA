using System.IO.Pipes;
using Avalonia;

namespace Arcline.SDA.Desktop;

internal static class Program
{
    /// <summary>Per-user pipe used to bring the running instance to the front.</summary>
    internal static string InstancePipeName => "ArclineSDA-" + Environment.UserName;

    internal static bool StartMinimized { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        StartMinimized = args.Contains(SteamGuard.Platform.Autostart.StartMinimizedArg);

        // One instance per user: two processes writing the same vault would race.
        using var mutex = new Mutex(true, "ArclineSDA-" + Environment.UserName, out var first);
        if (!first)
        {
            // Already running (e.g. started at login, in the tray): ask it to show its window instead.
            if (!StartMinimized) SignalRunningInstance();
            return 0;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void SignalRunningInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", InstancePipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            client.WriteByte(1);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Arcline SDA is already running.");
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
