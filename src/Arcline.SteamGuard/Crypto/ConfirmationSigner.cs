using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Arcline.SteamGuard.Crypto;

/// <summary>Signs mobile confirmation requests with the identity_secret.</summary>
public static class ConfirmationSigner
{
    public static string Sign(string identitySecret, long time, string tag)
    {
        ArgumentException.ThrowIfNullOrEmpty(identitySecret);
        var tagBytes = Encoding.UTF8.GetBytes(tag ?? string.Empty);
        var tagLen = Math.Min(tagBytes.Length, 32);
        var data = new byte[8 + tagLen];
        BinaryPrimitives.WriteInt64BigEndian(data, time);
        Array.Copy(tagBytes, 0, data, 8, tagLen);

        var key = SteamGuardCode.DecodeSecret(identitySecret);
        var hash = HMACSHA1.HashData(key, data);
        CryptographicOperations.ZeroMemory(key);
        return Convert.ToBase64String(hash);
    }
}
