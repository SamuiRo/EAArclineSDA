using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Arcline.SteamGuard.Platform;

/// <summary>
/// Optional "unlock automatically on this PC". The vault passphrase is kept in the OS's per-user secret store:
///   * Windows: DPAPI (CurrentUser), in %APPDATA%\ArclineSDA\autounlock.bin, bound to the vault path.
///   * Linux:   the Secret Service keyring (GNOME Keyring / KWallet) via `secret-tool` from libsecret.
/// Trade-off, stated in the UI: any program running as the same OS user can then open the vault on this PC.
/// Backups and exported files are unaffected; they still need the passphrase.
/// </summary>
public sealed class PassphraseStore
{
    private const string Application = "arcline-sda";
    private readonly string _windowsFile;

    public PassphraseStore(string? windowsFile = null) => _windowsFile = windowsFile ?? AppPaths.AutoUnlockFile;

    /// <summary>Null when available; otherwise why not (shown to the user).</summary>
    public string? UnavailableReason
    {
        get
        {
            if (OperatingSystem.IsWindows()) return null;
            if (OperatingSystem.IsLinux())
                return FindOnPath("secret-tool") is null
                    ? "Install libsecret's secret-tool (e.g. 'sudo apt install libsecret-tools' or 'sudo pacman -S libsecret') and a keyring such as GNOME Keyring or KWallet."
                    : null;
            return "Not supported on this OS.";
        }
    }

    public bool IsAvailable => UnavailableReason is null;

    public void Save(string vaultDir, string passphrase)
    {
        var path = Path.GetFullPath(vaultDir);
        if (OperatingSystem.IsWindows()) SaveWindows(path, passphrase);
        else if (OperatingSystem.IsLinux())
        {
            var (code, _, err) = Run("secret-tool", ["store", "--label=Arcline SDA vault", "application", Application, "vault", path], passphrase);
            if (code != 0) throw new SteamGuardException("Could not save to the system keyring: " + err.Trim());
        }
        else throw new SteamGuardException(UnavailableReason!);
    }

    public string? TryLoad(string vaultDir)
    {
        var path = Path.GetFullPath(vaultDir);
        try
        {
            if (OperatingSystem.IsWindows()) return LoadWindows(path);
            if (OperatingSystem.IsLinux() && IsAvailable)
            {
                var (code, output, _) = Run("secret-tool", ["lookup", "application", Application, "vault", path], null);
                return code == 0 && output.Length > 0 ? output.TrimEnd('\n') : null;
            }
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or JsonException or System.ComponentModel.Win32Exception) { }
        return null;
    }

    public bool IsSaved(string vaultDir) => TryLoad(vaultDir) is not null;

    /// <summary>Removes the stored passphrase (for this vault, or every vault when <paramref name="vaultDir"/> is null).</summary>
    public void Remove(string? vaultDir = null)
    {
        if (OperatingSystem.IsWindows())
        {
            if (File.Exists(_windowsFile)) File.Delete(_windowsFile);
        }
        else if (OperatingSystem.IsLinux() && IsAvailable)
        {
            string[] args = vaultDir is null
                ? ["clear", "application", Application]
                : ["clear", "application", Application, "vault", Path.GetFullPath(vaultDir)];
            Run("secret-tool", args, null);
        }
    }

    // ---------- Windows ----------

    [SupportedOSPlatform("windows")]
    private void SaveWindows(string vaultPath, string passphrase)
    {
        var plain = Encoding.UTF8.GetBytes(passphrase);
        try
        {
            var blob = ProtectedData.Protect(plain, Entropy(vaultPath), DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(_windowsFile)!);
            Storage.SecureFile.WriteAllText(_windowsFile, JsonSerializer.Serialize(new StoredSecret(vaultPath, Convert.ToBase64String(blob))));
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    [SupportedOSPlatform("windows")]
    private string? LoadWindows(string vaultPath)
    {
        if (!File.Exists(_windowsFile)) return null;
        var stored = JsonSerializer.Deserialize<StoredSecret>(File.ReadAllText(_windowsFile));
        if (stored is null || !string.Equals(stored.Vault, vaultPath, StringComparison.OrdinalIgnoreCase)) return null;
        var plain = ProtectedData.Unprotect(Convert.FromBase64String(stored.Blob), Entropy(vaultPath), DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static byte[] Entropy(string vaultPath) => Encoding.UTF8.GetBytes("arcline-sda-autounlock|" + vaultPath.ToUpperInvariant());

    private sealed record StoredSecret(string Vault, string Blob);

    // ---------- helpers ----------

    private static (int Code, string Output, string Error) Run(string exe, IEnumerable<string> args, string? stdin)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new SteamGuardException($"Could not start {exe}.");
        // The secret goes through stdin, never the command line (visible in the process list).
        if (stdin is not null) p.StandardInput.Write(stdin);
        p.StandardInput.Close();
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(15_000)) { p.Kill(); return (-1, "", "timed out"); }
        return (p.ExitCode, output, error);
    }

    private static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
}
