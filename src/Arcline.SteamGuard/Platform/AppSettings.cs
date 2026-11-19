using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arcline.SteamGuard.Platform;

public enum AppTheme { System, Light, Dark }

/// <summary>
/// Per-user app preferences that must be known before any vault is unlocked: the UI theme, and which vault
/// to open (vaults can live in any folder, like KeePassXC databases). Stored in <see cref="AppPaths.SettingsFile"/>.
/// It contains no secrets: only folder paths and the theme.
/// </summary>
public sealed class AppSettings
{
    public const int MaxRecentVaults = 8;

    [JsonPropertyName("theme")][JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))] public AppTheme Theme { get; set; } = AppTheme.System;
    [JsonPropertyName("last_vault")] public string? LastVault { get; set; }
    [JsonPropertyName("recent_vaults")] public List<string> RecentVaults { get; set; } = [];

    [JsonIgnore] public string FilePath { get; private set; } = AppPaths.SettingsFile;

    public static AppSettings Load(string? path = null)
    {
        path ??= AppPaths.SettingsFile;
        AppSettings? s = null;
        try
        {
            if (File.Exists(path)) s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json.Options);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        s ??= new AppSettings();
        s.FilePath = path;
        return s;
    }

    public void Save()
    {
        try { Storage.SecureFile.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json.Options)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Makes <paramref name="vaultDir"/> the vault opened next time and moves it to the top of the recent list.</summary>
    public void RememberVault(string vaultDir)
    {
        var full = Path.GetFullPath(vaultDir);
        LastVault = full;
        RecentVaults.RemoveAll(v => SamePath(v, full));
        RecentVaults.Insert(0, full);
        if (RecentVaults.Count > MaxRecentVaults) RecentVaults.RemoveRange(MaxRecentVaults, RecentVaults.Count - MaxRecentVaults);
        Save();
    }

    public void ForgetVault(string vaultDir)
    {
        var full = Path.GetFullPath(vaultDir);
        RecentVaults.RemoveAll(v => SamePath(v, full));
        if (LastVault is not null && SamePath(LastVault, full)) LastVault = RecentVaults.FirstOrDefault();
        Save();
    }

    /// <summary>Recent vault folders that still exist (a USB stick may be unplugged: those are skipped, not forgotten).</summary>
    public IReadOnlyList<string> ExistingRecentVaults() => RecentVaults.Where(Storage.MaFileVault.Exists).ToList();

    public static bool SamePath(string a, string b) => string.Equals(
        Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
