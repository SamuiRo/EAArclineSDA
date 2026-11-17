using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Arcline.SteamGuard.Crypto;

namespace Arcline.SteamGuard.Tests;

/// <summary>Checks the rewritten algorithms against verbatim ports of the original SteamAuth code.</summary>
public class CodeTests
{
    // --- Original SteamAuth implementation (SteamGuardAccount.GenerateSteamGuardCodeForTime) ---
    private static readonly byte[] Translations = [50, 51, 52, 53, 54, 55, 56, 57, 66, 67, 68, 70, 71, 72, 74, 75, 77, 78, 80, 81, 82, 84, 86, 87, 88, 89];

    private static string OriginalCode(string sharedSecret, long time)
    {
        var secret = Convert.FromBase64String(Regex.Unescape(sharedSecret));
        var timeArray = new byte[8];
        time /= 30L;
        for (var i = 8; i > 0; i--) { timeArray[i - 1] = (byte)time; time >>= 8; }
        using var h = new HMACSHA1 { Key = secret };
        var hashed = h.ComputeHash(timeArray);
        var code = new byte[5];
        var b = (byte)(hashed[19] & 0xF);
        var cp = (hashed[b] & 0x7F) << 24 | (hashed[b + 1] & 0xFF) << 16 | (hashed[b + 2] & 0xFF) << 8 | (hashed[b + 3] & 0xFF);
        for (var i = 0; i < 5; ++i) { code[i] = Translations[cp % Translations.Length]; cp /= Translations.Length; }
        return Encoding.UTF8.GetString(code);
    }

    private static string OriginalConfHash(string identitySecret, long time, string tag)
    {
        var decode = Convert.FromBase64String(identitySecret);
        var n2 = tag.Length > 32 ? 8 + 32 : 8 + tag.Length;
        var array = new byte[n2];
        var n3 = 8;
        while (true) { var n4 = n3 - 1; if (n3 <= 0) break; array[n4] = (byte)time; time >>= 8; n3 = n4; }
        Array.Copy(Encoding.UTF8.GetBytes(tag), 0, array, 8, n2 - 8);
        using var h = new HMACSHA1 { Key = decode };
        return WebUtility.UrlEncode(Convert.ToBase64String(h.ComputeHash(array)));
    }

    [Fact]
    public void SteamGuardCode_MatchesOriginalImplementation()
    {
        for (var i = 0; i < 500; i++)
        {
            var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(20));
            var time = 1_600_000_000L + Random.Shared.NextInt64(0, 400_000_000);
            Assert.Equal(OriginalCode(secret, time), SteamGuardCode.Generate(secret, time));
        }
    }

    [Fact]
    public void SteamGuardCode_HasExpectedShape()
    {
        var code = SteamGuardCode.Generate(Convert.ToBase64String(new byte[20]), 0);
        Assert.Matches("^[23456789BCDFGHJKMNPQRTVWXY]{5}$", code);
        Assert.Equal(30, SteamGuardCode.SecondsRemaining(60));
        Assert.Equal(1, SteamGuardCode.SecondsRemaining(89));
    }

    [Theory]
    [InlineData("conf")]
    [InlineData("accept")]
    [InlineData("reject")]
    [InlineData("a-very-long-tag-that-is-longer-than-thirty-two-characters")]
    public void ConfirmationSignature_MatchesOriginalImplementation(string tag)
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(20));
        const long time = 1_731_840_000;
        Assert.Equal(OriginalConfHash(secret, time, tag), WebUtility.UrlEncode(ConfirmationSigner.Sign(secret, time, tag)));
    }
}
