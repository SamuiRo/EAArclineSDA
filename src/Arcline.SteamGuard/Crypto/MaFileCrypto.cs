using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Arcline.SteamGuard.Models;

namespace Arcline.SteamGuard.Crypto;

/// <summary>
/// Encrypts/decrypts <see cref="SteamGuardAccount"/> objects into <see cref="MaFileEnvelope"/>s.
/// AES-256-GCM, key = PBKDF2-HMAC-SHA256(passphrase, salt, iterations, 32 bytes).
/// Derived keys are cached per (salt, iterations), so a vault that shares one salt derives the key once.
/// </summary>
public sealed class MaFileCrypto : IDisposable
{
    private readonly byte[] _passphrase;
    private readonly ConcurrentDictionary<string, byte[]> _keyCache = new();
    private bool _disposed;

    public MaFileCrypto(string passphrase)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        _passphrase = Encoding.UTF8.GetBytes(passphrase.Normalize(NormalizationForm.FormC));
    }

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(MaFileEnvelope.SaltSize);

    public MaFileEnvelope Encrypt(SteamGuardAccount account, byte[]? salt = null, int iterations = MaFileEnvelope.DefaultIterations)
        => Encrypt(Encoding.UTF8.GetBytes(account.ToJson(false)), account.AccountName, account.SteamId, salt, iterations);

    /// <summary>Encrypts <paramref name="plaintext"/>. The plaintext buffer is zeroed afterwards.</summary>
    public MaFileEnvelope Encrypt(byte[] plaintext, string? accountName, ulong steamId, byte[]? salt = null, int iterations = MaFileEnvelope.DefaultIterations)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, MaFileEnvelope.MinIterations);
        salt ??= NewSalt();
        var env = new MaFileEnvelope
        {
            AccountName = accountName,
            SteamId = steamId,
            Kdf = { Iterations = iterations, Salt = Convert.ToBase64String(salt) },
        };
        var nonce = RandomNumberGenerator.GetBytes(MaFileEnvelope.NonceSize);
        var tag = new byte[MaFileEnvelope.TagSize];
        var cipher = new byte[plaintext.Length];
        env.Cipher.Nonce = Convert.ToBase64String(nonce);

        using (var aes = new AesGcm(DeriveKey(salt, iterations), MaFileEnvelope.TagSize))
            aes.Encrypt(nonce, plaintext, cipher, tag, env.GetAad());

        CryptographicOperations.ZeroMemory(plaintext);
        env.Cipher.Tag = Convert.ToBase64String(tag);
        env.Ciphertext = Convert.ToBase64String(cipher);
        return env;
    }

    public SteamGuardAccount Decrypt(MaFileEnvelope env)
    {
        var plain = DecryptBytes(env);
        try { return SteamGuardAccount.Parse(Encoding.UTF8.GetString(plain)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public byte[] DecryptBytes(MaFileEnvelope env)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] salt, nonce, tag, cipher;
        try
        {
            salt = Convert.FromBase64String(env.Kdf.Salt);
            nonce = Convert.FromBase64String(env.Cipher.Nonce);
            tag = Convert.FromBase64String(env.Cipher.Tag);
            cipher = Convert.FromBase64String(env.Ciphertext);
        }
        catch (FormatException ex) { throw new SteamGuardException("Encrypted maFile is malformed.", ex); }
        if (nonce.Length != MaFileEnvelope.NonceSize || tag.Length != MaFileEnvelope.TagSize)
            throw new SteamGuardException("Encrypted maFile is malformed.");

        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(DeriveKey(salt, env.Kdf.Iterations), MaFileEnvelope.TagSize);
            aes.Decrypt(nonce, cipher, tag, plain, env.GetAad());
        }
        catch (CryptographicException) { throw new WrongPassphraseException(); }
        return plain;
    }

    private byte[] DeriveKey(byte[] salt, int iterations)
    {
        var cacheKey = Convert.ToBase64String(salt) + ":" + iterations;
        return _keyCache.GetOrAdd(cacheKey, _ =>
            Rfc2898DeriveBytes.Pbkdf2(_passphrase, salt, iterations, HashAlgorithmName.SHA256, MaFileEnvelope.KeySize));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_passphrase);
        foreach (var k in _keyCache.Values) CryptographicOperations.ZeroMemory(k);
        _keyCache.Clear();
    }
}
