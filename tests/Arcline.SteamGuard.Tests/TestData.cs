using System.Security.Cryptography;
using System.Text;
using Arcline.SteamGuard.Models;

namespace Arcline.SteamGuard.Tests;

internal static class TestData
{
    public static string FakeJwt(DateTimeOffset exp)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("{\"alg\":\"none\"}")}.{B64($"{{\"exp\":{exp.ToUnixTimeSeconds()}}}")}.sig";
    }

    public static SteamGuardAccount Account(ulong steamId = 76561198000000001, string name = "testacc") => new()
    {
        SharedSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(20)),
        IdentitySecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(20)),
        RevocationCode = "R12345",
        AccountName = name,
        DeviceId = "android:" + Guid.NewGuid(),
        SerialNumber = "1234567890",
        TokenGid = "abc",
        Secret1 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(20)),
        Uri = $"otpauth://totp/Steam:{name}?secret=XXXX&issuer=Steam",
        Status = 1,
        ServerTime = 1700000000,
        FullyEnrolled = true,
        Session = new SessionData
        {
            SteamId = steamId,
            AccessToken = FakeJwt(DateTimeOffset.UtcNow.AddHours(12)),
            RefreshToken = FakeJwt(DateTimeOffset.UtcNow.AddDays(150)),
            SessionId = "0123456789abcdef01234567",
        },
    };

    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "arcline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }
}
