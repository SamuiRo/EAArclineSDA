using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Arcline.SteamGuard.Platform;

/// <summary>
/// Start with the OS, minimized to the tray.
/// Windows: HKCU\Software\Microsoft\Windows\CurrentVersion\Run (current user only, no admin).
/// Linux:   $XDG_CONFIG_HOME/autostart/arcline-sda.desktop (XDG autostart spec).
/// </summary>
public sealed class Autostart
{
    public const string StartMinimizedArg = "--minimized";
    private const string WindowsRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ArclineSDA";

    private readonly string _windowsKey;
    private readonly string _linuxDir;

    /// <param name="windowsKey">HKCU subkey (tests use a scratch key).</param>
    /// <param name="linuxAutostartDir">XDG autostart directory (tests use a temp folder).</param>
    public Autostart(string? windowsKey = null, string? linuxAutostartDir = null)
    {
        _windowsKey = windowsKey ?? WindowsRunKey;
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(xdg)) xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        _linuxDir = linuxAutostartDir ?? Path.Combine(xdg, "autostart");
    }

    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    public string LinuxDesktopFile => Path.Combine(_linuxDir, "arcline-sda.desktop");

    public bool IsEnabled()
    {
        if (OperatingSystem.IsWindows()) return WindowsValue() is not null;
        if (OperatingSystem.IsLinux()) return File.Exists(LinuxDesktopFile);
        return false;
    }

    /// <summary>True when enabled AND pointing at this executable (false after the app folder was moved).</summary>
    public bool IsEnabledForThisExecutable(string executable)
    {
        if (OperatingSystem.IsWindows()) return WindowsValue()?.Contains(executable, StringComparison.OrdinalIgnoreCase) == true;
        if (OperatingSystem.IsLinux()) return File.Exists(LinuxDesktopFile) && File.ReadAllText(LinuxDesktopFile).Contains(executable, StringComparison.Ordinal);
        return false;
    }

    public void Enable(string executable)
    {
        if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
            throw new SteamGuardException("Cannot find the application executable for autostart.");
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.CreateSubKey(_windowsKey, writable: true);
            key.SetValue(ValueName, $"\"{executable}\" {StartMinimizedArg}", RegistryValueKind.String);
        }
        else if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(_linuxDir);
            var exec = executable.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
            File.WriteAllText(LinuxDesktopFile,
                "[Desktop Entry]\n" +
                "Type=Application\n" +
                "Name=Arcline SDA\n" +
                "Comment=Steam Guard authenticator\n" +
                $"Exec=\"{exec}\" {StartMinimizedArg}\n" +
                "Terminal=false\n" +
                "X-GNOME-Autostart-enabled=true\n");
        }
        else throw new SteamGuardException("Autostart is not supported on this OS.");
    }

    public void Disable()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(_windowsKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        else if (OperatingSystem.IsLinux() && File.Exists(LinuxDesktopFile))
        {
            File.Delete(LinuxDesktopFile);
        }
    }

    [SupportedOSPlatform("windows")]
    private string? WindowsValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_windowsKey);
        return key?.GetValue(ValueName) as string;
    }
}
