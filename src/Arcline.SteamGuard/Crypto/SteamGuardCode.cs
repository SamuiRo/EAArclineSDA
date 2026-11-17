using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Arcline.SteamGuard.Crypto;

/// <summary>Steam's 5-character TOTP variant (RFC 6238 with a custom alphabet, 30 s period).</summary>
public static class SteamGuardCode
{
    private const string Alphabet = "23456789BCDFGHJKMNPQRTVWXY";
    public const int PeriodSeconds = 30;

    public static string Generate(string sharedSecret, long steamTimeSeconds)
    {
        ArgumentException.ThrowIfNullOrEmpty(sharedSecret);
        var key = DecodeSecret(sharedSecret);
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, steamTimeSeconds / PeriodSeconds);
        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(key, counter, hash);
        CryptographicOperations.ZeroMemory(key);

        var offset = hash[^1] & 0x0F;
        var code = BinaryPrimitives.ReadInt32BigEndian(hash.Slice(offset, 4)) & 0x7FFFFFFF;
        Span<char> chars = stackalloc char[5];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[code % Alphabet.Length];
            code /= Alphabet.Length;
        }
        return new string(chars);
    }

    public static int SecondsRemaining(long steamTimeSeconds) => PeriodSeconds - (int)(steamTimeSeconds % PeriodSeconds);

    /// <summary>Secrets are Base64. Very old maFiles JSON-escaped slashes, which the JSON parser already unescapes.</summary>
    internal static byte[] DecodeSecret(string secret) => Convert.FromBase64String(secret.Replace("\\/", "/").Trim());
}
