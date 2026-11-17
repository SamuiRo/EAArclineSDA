using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Arcline.SteamGuard.Crypto;
using Arcline.SteamGuard.Legacy;
using Arcline.SteamGuard.Models;
using Arcline.SteamGuard.Steam;

namespace Arcline.SteamGuard.Storage;

public enum ExportMode
{
    /// <summary>Standalone arcline-mafile envelope protected by a separate transfer passphrase (recommended).</summary>
    Encrypted,
    /// <summary>Raw SDA/SteamAuth-compatible JSON. Anyone who reads it owns the account.</summary>
    Plaintext,
}

/// <summary>
/// A directory of encrypted maFiles. Encryption is mandatory: there is no unencrypted mode,
/// every account file is an <see cref="MaFileEnvelope"/>, and plaintext only leaves the vault
/// through an explicit <see cref="Export"/> with <see cref="ExportMode.Plaintext"/>.
/// </summary>
public sealed class MaFileVault : IAccountStateStore, IDisposable
{
    public const string IndexFileName = "vault.json";
    public const int MinPassphraseLength = 8;
    private static readonly byte[] VerifierPlaintext = "arcline-vault-verifier-v1"u8.ToArray();

    private readonly Lock _sync = new();
    private MaFileCrypto? _crypto;
    private byte[] _salt = [];

    private MaFileVault(string directory, VaultIndex index)
    {
        Directory = Path.GetFullPath(directory);
        Index = index;
        _salt = Convert.FromBase64String(index.Kdf.Salt);
    }

    public string Directory { get; }
    public VaultIndex Index { get; }
    public VaultSettings Settings => Index.Settings;
    public bool IsUnlocked => _crypto is not null;
    public IReadOnlyList<VaultEntry> Entries { get { lock (_sync) return Index.Accounts.ToList(); } }

    // ---------- location ----------

    /// <summary>
    /// Vault directory: $ARCLINE_SDA_VAULT, else portable "vault" folder next to the executable if it exists,
    /// else the per-user config dir (%APPDATA%\ArclineSDA\vault on Windows, ~/.config/ArclineSDA/vault on Linux).
    /// </summary>
    public static string DefaultDirectory()
    {
        var env = Environment.GetEnvironmentVariable("ARCLINE_SDA_VAULT");
        if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
        var portable = Path.Combine(AppContext.BaseDirectory, "vault");
        if (File.Exists(Path.Combine(portable, IndexFileName))) return portable;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "ArclineSDA", "vault");
    }

    public static bool Exists(string directory) => File.Exists(Path.Combine(directory, IndexFileName));

    public static MaFileVault Create(string directory, string passphrase, int iterations = MaFileEnvelope.DefaultIterations)
    {
        ValidateNewPassphrase(passphrase);
        if (Exists(directory)) throw new SteamGuardException($"A vault already exists in '{directory}'.");
        SecureFile.EnsureDirectory(directory);
        var salt = MaFileCrypto.NewSalt();
        var index = new VaultIndex { Kdf = { Salt = Convert.ToBase64String(salt), Iterations = iterations } };
        var vault = new MaFileVault(directory, index) { _crypto = new MaFileCrypto(passphrase) };
        index.Verifier = vault._crypto.Encrypt(VerifierPlaintext.ToArray(), "verifier", 0, salt, iterations);
        vault.SaveIndex();
        return vault;
    }

    public static MaFileVault Open(string directory)
    {
        var path = Path.Combine(directory, IndexFileName);
        if (!File.Exists(path)) throw new SteamGuardException($"No vault found in '{directory}'.");
        var index = JsonSerializer.Deserialize<VaultIndex>(File.ReadAllText(path), Json.Options)
                    ?? throw new SteamGuardException("vault.json is empty.");
        if (index.Format != VaultIndex.FormatName) throw new SteamGuardException("vault.json has an unknown format.");
        return new MaFileVault(directory, index);
    }

    public static void ValidateNewPassphrase(string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase) || passphrase.Length < MinPassphraseLength)
            throw new SteamGuardException($"Passphrase must be at least {MinPassphraseLength} characters.");
    }

    // ---------- lock / unlock ----------

    public void Unlock(string passphrase)
    {
        var crypto = new MaFileCrypto(passphrase);
        try
        {
            if (Index.Verifier is null) throw new SteamGuardException("vault.json has no verifier.");
            CryptographicOperations.ZeroMemory(crypto.DecryptBytes(Index.Verifier));
        }
        catch
        {
            crypto.Dispose();
            throw;
        }
        lock (_sync)
        {
            _crypto?.Dispose();
            _crypto = crypto;
        }
    }

    /// <summary>Checks a passphrase without changing the unlocked state.</summary>
    public bool VerifyPassphrase(string passphrase)
    {
        if (Index.Verifier is null || string.IsNullOrEmpty(passphrase)) return false;
        using var c = new MaFileCrypto(passphrase);
        try { CryptographicOperations.ZeroMemory(c.DecryptBytes(Index.Verifier)); return true; }
        catch (WrongPassphraseException) { return false; }
    }

    public void Lock()
    {
        lock (_sync)
        {
            _crypto?.Dispose();
            _crypto = null;
        }
    }

    private MaFileCrypto Crypto => _crypto ?? throw new SteamGuardException("Vault is locked.");

    // ---------- accounts ----------

    public VaultEntry Resolve(string nameOrSteamId)
    {
        lock (_sync)
        {
            if (ulong.TryParse(nameOrSteamId, out var id))
            {
                var byId = Index.Accounts.FirstOrDefault(a => a.SteamId == id);
                if (byId is not null) return byId;
            }
            return Index.Accounts.FirstOrDefault(a => string.Equals(a.AccountName, nameOrSteamId, StringComparison.OrdinalIgnoreCase))
                   ?? throw new SteamGuardException($"Account '{nameOrSteamId}' is not in the vault.");
        }
    }

    public SteamGuardAccount Load(ulong steamId)
    {
        VaultEntry entry;
        lock (_sync) entry = Index.Accounts.FirstOrDefault(a => a.SteamId == steamId) ?? throw new SteamGuardException($"SteamID {steamId} is not in the vault.");
        var env = MaFileEnvelope.Parse(File.ReadAllText(Path.Combine(Directory, SafeName(entry.File))));
        if (env.SteamId != steamId) throw new SteamGuardException($"{entry.File} belongs to a different account.");
        return Crypto.Decrypt(env);
    }

    public IReadOnlyList<SteamGuardAccount> LoadAll() => Entries.Select(e => Load(e.SteamId)).ToList();

    /// <summary>Encrypts and writes the account (atomic). Adds it to the index if new.</summary>
    public void Save(SteamGuardAccount account, bool allowOverwrite = true)
    {
        if (account.SteamId == 0) throw new SteamGuardException("maFile has no SteamID (Session.SteamID). Log in to the account first.");
        if (string.IsNullOrEmpty(account.SharedSecret)) throw new SteamGuardException("maFile has no shared_secret.");
        lock (_sync)
        {
            var entry = Index.Accounts.FirstOrDefault(a => a.SteamId == account.SteamId);
            if (entry is not null && !allowOverwrite) throw new SteamGuardException($"Account {account.DisplayName} is already in the vault.");
            var env = Crypto.Encrypt(account, _salt, Index.Kdf.Iterations);
            var file = entry?.File ?? $"{account.SteamId}.maFile";
            SecureFile.WriteAllText(Path.Combine(Directory, file), env.ToJson());
            if (entry is null)
            {
                Index.Accounts.Add(new VaultEntry { SteamId = account.SteamId, AccountName = account.AccountName, File = file });
            }
            else
            {
                entry.AccountName = account.AccountName ?? entry.AccountName;
            }
            SaveIndex();
        }
    }

    /// <summary>Removes the account from the vault. The encrypted file is moved to backups/ unless <paramref name="keepBackup"/> is false.</summary>
    public void Remove(ulong steamId, bool keepBackup = true)
    {
        lock (_sync)
        {
            var entry = Index.Accounts.FirstOrDefault(a => a.SteamId == steamId) ?? throw new SteamGuardException($"SteamID {steamId} is not in the vault.");
            var path = Path.Combine(Directory, entry.File);
            if (File.Exists(path))
            {
                if (keepBackup)
                {
                    var backups = Path.Combine(Directory, "backups");
                    SecureFile.EnsureDirectory(backups);
                    File.Move(path, Path.Combine(backups, $"{steamId}-{DateTime.UtcNow:yyyyMMddHHmmss}.maFile"));
                }
                else File.Delete(path);
            }
            Index.Accounts.Remove(entry);
            SaveIndex();
        }
    }

    public void Move(int from, int to)
    {
        lock (_sync)
        {
            if (from < 0 || from >= Index.Accounts.Count || to < 0 || to >= Index.Accounts.Count) return;
            var e = Index.Accounts[from];
            Index.Accounts.RemoveAt(from);
            Index.Accounts.Insert(to, e);
            SaveIndex();
        }
    }

    /// <summary>Re-encrypts every file with a new passphrase and a fresh salt. All files are written to temp first.</summary>
    public void ChangePassphrase(string newPassphrase)
    {
        ValidateNewPassphrase(newPassphrase);
        lock (_sync)
        {
            var accounts = Index.Accounts.Select(e => (e, acc: Load(e.SteamId))).ToList();
            var newSalt = MaFileCrypto.NewSalt();
            var newCrypto = new MaFileCrypto(newPassphrase);
            var staged = accounts.Select(x => (x.e, json: newCrypto.Encrypt(x.acc, newSalt, Index.Kdf.Iterations).ToJson())).ToList();
            var verifier = newCrypto.Encrypt(VerifierPlaintext.ToArray(), "verifier", 0, newSalt, Index.Kdf.Iterations);

            foreach (var (e, json) in staged) SecureFile.WriteAllText(Path.Combine(Directory, e.File), json);
            Index.Kdf.Salt = Convert.ToBase64String(newSalt);
            Index.Verifier = verifier;
            _salt = newSalt;
            SaveIndex();
            _crypto?.Dispose();
            _crypto = newCrypto;
        }
    }

    // ---------- export / import ----------

    /// <summary>
    /// Writes one account to <paramref name="path"/> for use elsewhere (one-off transfer to another program/PC).
    /// Encrypted mode produces a standalone file with its own salt, decryptable with only the transfer passphrase.
    /// </summary>
    public void Export(ulong steamId, string path, ExportMode mode, string? transferPassphrase = null)
    {
        var account = Load(steamId);
        string contents;
        if (mode == ExportMode.Encrypted)
        {
            ValidateNewPassphrase(transferPassphrase ?? "");
            using var c = new MaFileCrypto(transferPassphrase!);
            contents = c.Encrypt(account, MaFileCrypto.NewSalt(), Index.Kdf.Iterations).ToJson();
        }
        else
        {
            contents = account.ToJson();
        }
        SecureFile.WriteAllText(path, contents);
    }

    /// <summary>Reads a maFile in any supported form: arcline envelope (needs its passphrase) or plaintext SDA JSON.</summary>
    public static SteamGuardAccount ReadExternalFile(string path, string? filePassphrase)
    {
        var text = File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿');
        if (MaFileEnvelope.LooksLikeEnvelope(text))
        {
            if (string.IsNullOrEmpty(filePassphrase)) throw new SteamGuardException("This maFile is encrypted; its passphrase is required.");
            using var c = new MaFileCrypto(filePassphrase);
            return c.Decrypt(MaFileEnvelope.Parse(text));
        }
        try { return SteamGuardAccount.Parse(text); }
        catch (JsonException ex)
        {
            throw new SteamGuardException("Not a maFile. If it is an old SDA encrypted file, import the whole SDA folder instead (it needs manifest.json).", ex);
        }
    }

    public SteamGuardAccount Import(string path, string? filePassphrase = null, bool overwrite = false, bool shredSource = false)
    {
        var account = ReadExternalFile(path, filePassphrase);
        Save(account, overwrite);
        if (shredSource) SecureFile.Shred(path);
        return account;
    }

    // ---------- migration between PCs ----------

    /// <summary>
    /// Writes the whole vault (vault.json + encrypted maFiles, byte for byte) into one .zip.
    /// The backup stays encrypted; it opens with the same vault passphrase on any OS.
    /// </summary>
    public void Backup(string zipPath)
    {
        lock (_sync)
        {
            var full = Path.GetFullPath(zipPath);
            var tmp = full + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(Path.Combine(Directory, IndexFileName), IndexFileName, CompressionLevel.Optimal);
                foreach (var e in Index.Accounts)
                {
                    var src = Path.Combine(Directory, e.File);
                    if (File.Exists(src)) zip.CreateEntryFromFile(src, e.File, CompressionLevel.Optimal);
                }
            }
            File.Move(tmp, full, overwrite: true);
        }
    }

    /// <summary>
    /// New PC: recreates a vault from a backup .zip (or a copied vault folder) in <paramref name="targetDir"/>.
    /// The passphrase stays the same. Fails if a vault already exists there (use <see cref="ImportVault"/> to merge).
    /// </summary>
    public static void Restore(string source, string targetDir)
    {
        if (Exists(targetDir)) throw new SteamGuardException($"A vault already exists in '{targetDir}'. Unlock it and use \"Import from another vault\" to merge instead.");
        WithVaultSource(source, srcDir =>
        {
            var index = Open(srcDir).Index; // validates vault.json
            SecureFile.EnsureDirectory(targetDir);
            foreach (var e in index.Accounts)
            {
                var name = SafeName(e.File);
                var from = Path.Combine(srcDir, name);
                if (File.Exists(from)) SecureFile.WriteAllText(Path.Combine(targetDir, name), File.ReadAllText(from));
            }
            // index last: a half-finished restore is not a usable vault
            SecureFile.WriteAllText(Path.Combine(targetDir, IndexFileName), File.ReadAllText(Path.Combine(srcDir, IndexFileName)));
        });
    }

    /// <summary>Merges every account of another vault (folder or backup .zip, opened with ITS passphrase) into this one.</summary>
    public LegacySdaImporter.ImportResult ImportVault(string source, string sourcePassphrase, bool overwrite = false)
    {
        var imported = new List<SteamGuardAccount>();
        var errors = new List<string>();
        WithVaultSource(source, srcDir =>
        {
            using var other = Open(srcDir);
            other.Unlock(sourcePassphrase);
            foreach (var e in other.Entries)
            {
                try
                {
                    var acc = other.Load(e.SteamId);
                    Save(acc, overwrite);
                    if (e.TradeProtectionAcknowledged) SetTradeProtectionAcknowledgedAsync(e.SteamId, true, default).GetAwaiter().GetResult();
                    imported.Add(acc);
                }
                catch (Exception ex) when (ex is SteamGuardException or IOException or JsonException)
                {
                    errors.Add($"{e.DisplayName}: {ex.Message}");
                }
            }
        });
        return new LegacySdaImporter.ImportResult(imported, errors);
    }

    /// <summary>True for a folder containing vault.json or a .zip produced by <see cref="Backup"/>.</summary>
    public static bool IsVaultSource(string path) =>
        System.IO.Directory.Exists(path) ? Exists(path)
        : File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    private static void WithVaultSource(string source, Action<string> action)
    {
        if (System.IO.Directory.Exists(source))
        {
            if (!Exists(source)) throw new SteamGuardException($"'{source}' is not an Arcline vault folder (no {IndexFileName}).");
            action(source);
            return;
        }
        if (!File.Exists(source)) throw new SteamGuardException($"'{source}' does not exist.");

        var tmp = Path.Combine(Path.GetTempPath(), "arcline-restore-" + Guid.NewGuid().ToString("N"));
        SecureFile.EnsureDirectory(tmp);
        try
        {
            using (var zip = ZipFile.OpenRead(source))
            {
                foreach (var entry in zip.Entries)
                {
                    // Only flat vault.json / *.maFile entries; anything else (paths, other files) is ignored.
                    var name = Path.GetFileName(entry.FullName);
                    if (name != entry.FullName) continue;
                    if (name != IndexFileName && !name.EndsWith(".maFile", StringComparison.OrdinalIgnoreCase)) continue;
                    if (entry.Length > 1_000_000) throw new SteamGuardException($"{name} in the backup is suspiciously large.");
                    entry.ExtractToFile(Path.Combine(tmp, name));
                }
            }
            if (!Exists(tmp)) throw new SteamGuardException("This .zip is not an Arcline vault backup (no vault.json).");
            action(tmp);
        }
        catch (InvalidDataException ex) { throw new SteamGuardException("The backup file is not a valid .zip.", ex); }
        finally { System.IO.Directory.Delete(tmp, recursive: true); }
    }

    private static string SafeName(string file)
    {
        var name = Path.GetFileName(file);
        if (string.IsNullOrEmpty(name) || name != file) throw new SteamGuardException($"Invalid file name in vault.json: '{file}'.");
        return name;
    }

    public LegacySdaImporter.ImportResult ImportLegacySda(string sdaFolder, string? legacyPassKey, bool overwrite = false)
    {
        var result = LegacySdaImporter.Load(sdaFolder, legacyPassKey);
        var errors = result.Errors.ToList();
        var imported = new List<SteamGuardAccount>();
        foreach (var acc in result.Accounts)
        {
            try { Save(acc, overwrite); imported.Add(acc); }
            catch (SteamGuardException ex) { errors.Add($"{acc.DisplayName}: {ex.Message}"); }
        }
        return new LegacySdaImporter.ImportResult(imported, errors);
    }

    /// <summary>Plaintext *.maFile files lying in the vault directory (e.g. dropped in manually). They should be imported and shredded.</summary>
    public IReadOnlyList<string> FindPlaintextFiles() =>
        System.IO.Directory.EnumerateFiles(Directory, "*.maFile")
            .Where(f => !MaFileEnvelope.LooksLikeEnvelope(File.ReadAllText(f)))
            .ToList();

    // ---------- IAccountStateStore ----------

    Task IAccountStateStore.SessionUpdatedAsync(SteamGuardAccount account, CancellationToken ct)
    {
        if (IsUnlocked && account.SteamId != 0 && Index.Accounts.Any(a => a.SteamId == account.SteamId)) Save(account);
        return Task.CompletedTask;
    }

    public bool IsTradeProtectionAcknowledged(ulong steamId)
    {
        lock (_sync) return Index.Accounts.FirstOrDefault(a => a.SteamId == steamId)?.TradeProtectionAcknowledged ?? false;
    }

    public Task SetTradeProtectionAcknowledgedAsync(ulong steamId, bool value, CancellationToken ct)
    {
        lock (_sync)
        {
            var e = Index.Accounts.FirstOrDefault(a => a.SteamId == steamId);
            if (e is not null && e.TradeProtectionAcknowledged != value)
            {
                e.TradeProtectionAcknowledged = value;
                SaveIndex();
            }
        }
        return Task.CompletedTask;
    }

    public void SaveIndex()
    {
        lock (_sync) SecureFile.WriteAllText(Path.Combine(Directory, IndexFileName), JsonSerializer.Serialize(Index, Json.Options));
    }

    public void Dispose() => Lock();
}
