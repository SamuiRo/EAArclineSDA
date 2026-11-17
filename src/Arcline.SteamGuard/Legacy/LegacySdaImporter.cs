using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arcline.SteamGuard.Crypto;
using Arcline.SteamGuard.Models;

namespace Arcline.SteamGuard.Legacy;

/// <summary>
/// Reads a classic Steam Desktop Authenticator "maFiles" folder (manifest.json + *.maFile),
/// including SDA-encrypted files (PBKDF2-SHA1 50k, AES-256-CBC, salt/IV stored in manifest.json).
/// </summary>
public static class LegacySdaImporter
{
    private const int LegacyIterations = 50_000;

    public sealed record ImportResult(IReadOnlyList<SteamGuardAccount> Accounts, IReadOnlyList<string> Errors);

    /// <summary>Accepts either the SDA install folder or its maFiles subfolder.</summary>
    public static string ResolveMaFilesDir(string path)
    {
        if (File.Exists(Path.Combine(path, "manifest.json"))) return path;
        var sub = Path.Combine(path, "maFiles");
        if (File.Exists(Path.Combine(sub, "manifest.json"))) return sub;
        throw new SteamGuardException($"No SDA manifest.json found in '{path}' or '{sub}'.");
    }

    public static bool IsEncrypted(string maFilesDir) => ReadManifest(maFilesDir).Encrypted;

    public static ImportResult Load(string path, string? legacyPassKey)
    {
        var dir = ResolveMaFilesDir(path);
        var manifest = ReadManifest(dir);
        if (manifest.Encrypted && string.IsNullOrEmpty(legacyPassKey))
            throw new SteamGuardException("This SDA folder is encrypted. Provide its old passkey.");

        var accounts = new List<SteamGuardAccount>();
        var errors = new List<string>();
        foreach (var e in manifest.Entries ?? [])
        {
            var file = Path.Combine(dir, Path.GetFileName(e.FileName ?? ""));
            try
            {
                var text = File.ReadAllText(file);
                if (manifest.Encrypted)
                {
                    if (string.IsNullOrEmpty(e.Salt) || string.IsNullOrEmpty(e.Iv))
                        throw new SteamGuardException("missing encryption_salt / encryption_iv in manifest.json");
                    text = Decrypt(legacyPassKey!, e.Salt, e.Iv, text)
                           ?? throw new WrongPassphraseException();
                }
                accounts.Add(SteamGuardAccount.Parse(text));
            }
            catch (Exception ex) when (ex is IOException or SteamGuardException or JsonException or FormatException)
            {
                errors.Add($"{e.FileName}: {ex.Message}");
            }
        }
        return new ImportResult(accounts, errors);
    }

    /// <summary>Decrypts one SDA-encrypted maFile body. Returns null for a wrong passkey.</summary>
    public static string? Decrypt(string passKey, string saltB64, string ivB64, string cipherB64)
    {
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passKey), Convert.FromBase64String(saltB64),
            LegacyIterations, HashAlgorithmName.SHA1, 32);
        try
        {
            using var aes = Aes.Create();
            aes.Key = key;
            var plain = aes.DecryptCbc(Convert.FromBase64String(cipherB64.Trim()), Convert.FromBase64String(ivB64), PaddingMode.PKCS7);
            var text = Encoding.UTF8.GetString(plain);
            CryptographicOperations.ZeroMemory(plain);
            return text.TrimStart('﻿');
        }
        catch (CryptographicException) { return null; }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>Produces SDA-format ciphertext (used by tests and for round-trip verification).</summary>
    public static string Encrypt(string passKey, string saltB64, string ivB64, string plaintext)
    {
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passKey), Convert.FromBase64String(saltB64),
            LegacyIterations, HashAlgorithmName.SHA1, 32);
        using var aes = Aes.Create();
        aes.Key = key;
        var c = aes.EncryptCbc(Encoding.UTF8.GetBytes(plaintext), Convert.FromBase64String(ivB64), PaddingMode.PKCS7);
        CryptographicOperations.ZeroMemory(key);
        return Convert.ToBase64String(c);
    }

    private static LegacyManifest ReadManifest(string dir)
    {
        var json = File.ReadAllText(Path.Combine(dir, "manifest.json"));
        return JsonSerializer.Deserialize<LegacyManifest>(json, Json.Options)
               ?? throw new SteamGuardException("SDA manifest.json is empty.");
    }

    private sealed class LegacyManifest
    {
        [JsonPropertyName("encrypted")] public bool Encrypted { get; set; }
        [JsonPropertyName("entries")] public List<LegacyManifestEntry>? Entries { get; set; }
    }

    private sealed class LegacyManifestEntry
    {
        [JsonPropertyName("encryption_iv")] public string? Iv { get; set; }
        [JsonPropertyName("encryption_salt")] public string? Salt { get; set; }
        [JsonPropertyName("filename")] public string? FileName { get; set; }
        [JsonPropertyName("steamid")] public ulong SteamId { get; set; }
    }
}
