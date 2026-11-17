using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arcline.SteamGuard.Crypto;

/// <summary>
/// Self-contained encrypted maFile ("arcline-mafile" v1). Everything needed to decrypt it,
/// except the passphrase, is inside the file; no manifest.json is required.
/// See docs/features/MAFILE_FORMAT.md for the language-neutral specification.
/// </summary>
public sealed class MaFileEnvelope
{
    public const string FormatName = "arcline-mafile";
    public const int CurrentVersion = 1;
    public const string KdfPbkdf2Sha256 = "pbkdf2-sha256";
    public const string CipherAes256Gcm = "aes-256-gcm";
    public const int DefaultIterations = 600_000;
    public const int MinIterations = 100_000;
    public const int SaltSize = 16;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int KeySize = 32;

    [JsonPropertyName("format")] public string Format { get; set; } = FormatName;
    [JsonPropertyName("version")] public int Version { get; set; } = CurrentVersion;
    [JsonPropertyName("account_name")] public string? AccountName { get; set; }
    [JsonPropertyName("steamid")] public ulong SteamId { get; set; }
    [JsonPropertyName("kdf")] public KdfParams Kdf { get; set; } = new();
    [JsonPropertyName("cipher")] public CipherParams Cipher { get; set; } = new();
    [JsonPropertyName("ciphertext")] public string Ciphertext { get; set; } = "";

    public sealed class KdfParams
    {
        [JsonPropertyName("name")] public string Name { get; set; } = KdfPbkdf2Sha256;
        [JsonPropertyName("iterations")] public int Iterations { get; set; } = DefaultIterations;
        [JsonPropertyName("salt")] public string Salt { get; set; } = "";
    }

    public sealed class CipherParams
    {
        [JsonPropertyName("name")] public string Name { get; set; } = CipherAes256Gcm;
        [JsonPropertyName("nonce")] public string Nonce { get; set; } = "";
        [JsonPropertyName("tag")] public string Tag { get; set; } = "";
    }

    /// <summary>
    /// Additional authenticated data: binds the clear-text header to the ciphertext so
    /// account_name / steamid / KDF parameters cannot be swapped without detection.
    /// </summary>
    public byte[] GetAad() => Encoding.UTF8.GetBytes(
        $"{FormatName}|{Version}|{Kdf.Name}|{Kdf.Iterations}|{Kdf.Salt}|{SteamId}|{AccountName ?? ""}");

    public static bool LooksLikeEnvelope(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("format", out var f)
                && f.ValueKind == JsonValueKind.String
                && f.GetString() == FormatName;
        }
        catch (JsonException) { return false; }
    }

    public static MaFileEnvelope Parse(string json)
    {
        var env = JsonSerializer.Deserialize<MaFileEnvelope>(json, Json.Options)
                  ?? throw new SteamGuardException("Encrypted maFile is empty.");
        if (env.Format != FormatName) throw new SteamGuardException($"Unknown file format '{env.Format}'.");
        if (env.Version != CurrentVersion) throw new SteamGuardException($"Unsupported {FormatName} version {env.Version}.");
        if (env.Kdf.Name != KdfPbkdf2Sha256) throw new SteamGuardException($"Unsupported KDF '{env.Kdf.Name}'.");
        if (env.Cipher.Name != CipherAes256Gcm) throw new SteamGuardException($"Unsupported cipher '{env.Cipher.Name}'.");
        if (env.Kdf.Iterations < MinIterations) throw new SteamGuardException("KDF iteration count is too low.");
        return env;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json.Options);
}
