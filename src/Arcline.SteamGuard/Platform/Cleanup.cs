namespace Arcline.SteamGuard.Platform;

/// <summary>
/// Removes everything this app put on the PC outside its own folder. The vault is removed only when explicitly
/// asked for. Used by "File > Remove app data from this PC" and `arcline-sda cleanup`.
/// </summary>
public static class Cleanup
{
    public sealed record Report(IReadOnlyList<string> Removed, IReadOnlyList<string> Failed);

    public static Report Run(bool includeVault, string? vaultDir = null, Autostart? autostart = null, PassphraseStore? store = null)
    {
        var removed = new List<string>();
        var failed = new List<string>();

        void Try(string what, Action action)
        {
            try { action(); removed.Add(what); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SteamGuardException or System.Security.SecurityException)
            {
                failed.Add($"{what}: {ex.Message}");
            }
        }

        autostart ??= new Autostart();
        if (autostart.IsEnabled()) Try("autostart entry", autostart.Disable);

        store ??= new PassphraseStore();
        Try("saved auto-unlock passphrase", () => store.Remove());

        if (Directory.Exists(AppPaths.Logs)) Try(AppPaths.Logs, () => Directory.Delete(AppPaths.Logs, recursive: true));

        foreach (var dir in AppPaths.LegacyExtractionDirs().Where(Directory.Exists))
            Try(dir, () => Directory.Delete(dir, recursive: true));

        if (includeVault)
        {
            var vault = vaultDir ?? AppPaths.DefaultVault;
            if (Directory.Exists(vault)) Try(vault, () => Directory.Delete(vault, recursive: true));
        }

        // Remove the app's data folder if nothing else is left in it.
        if (Directory.Exists(AppPaths.Root) && !Directory.EnumerateFileSystemEntries(AppPaths.Root).Any())
            Try(AppPaths.Root, () => Directory.Delete(AppPaths.Root));

        return new Report(removed, failed);
    }
}
