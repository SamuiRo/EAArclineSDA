using Avalonia;

namespace Arcline.SDA.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // One instance per user: two processes writing the same vault would race.
        using var mutex = new Mutex(true, "ArclineSDA-" + Environment.UserName, out var first);
        if (!first)
        {
            Console.Error.WriteLine("Arcline SDA is already running.");
            return 1;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
