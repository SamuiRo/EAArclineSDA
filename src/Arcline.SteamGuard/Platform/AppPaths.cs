namespace Arcline.SteamGuard.Platform;

/// <summary>
/// Every place this app writes outside its own folder. Keep this list complete: <see cref="Cleanup"/> and the
/// README's "What is stored where" section rely on it.
/// </summary>
public static class AppPaths
{
    /// <summary>%APPDATA%\ArclineSDA on Windows, ~/.config/ArclineSDA on Linux.</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "ArclineSDA");

    public static string DefaultVault => Path.Combine(Root, "vault");
    public static string Logs => Path.Combine(Root, "logs");

    /// <summary>Windows only: DPAPI-protected passphrase for "unlock automatically on this PC".</summary>
    public static string AutoUnlockFile => Path.Combine(Root, "autounlock.bin");

    /// <summary>
    /// Where versions up to 2.4.0 had .NET extract bundled native libraries (single-file self-extract).
    /// Newer builds ship those libraries next to the executable, but old copies may still be here.
    /// </summary>
    public static IEnumerable<string> LegacyExtractionDirs()
    {
        var baseDir = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR");
        if (string.IsNullOrEmpty(baseDir))
        {
            baseDir = OperatingSystem.IsWindows()
                ? Path.Combine(Path.GetTempPath(), ".net")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".net");
        }
        yield return Path.Combine(baseDir, "ArclineSDA");
        yield return Path.Combine(baseDir, "arcline-sda");
    }
}
