using System.Net;
using System.Security.Cryptography;
using System.Text;
using Arcline.SteamGuard.Models;
using Arcline.SteamGuard.Steam;

namespace Arcline.SteamGuard.Tests;

/// <summary>Moving an existing authenticator here (RemoveAuthenticatorViaChallengeStart/Continue) against a fake Steam.</summary>
public class TransferTests
{
    private sealed class FakeApi(Func<string, string, (string Body, int EResult)> respond) : HttpMessageHandler
    {
        public List<(string Path, string Query, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.AbsolutePath;
            Calls.Add((path, request.RequestUri.Query, body));
            var (json, eresult) = path == "/ITwoFactorService/QueryTime/v1/"
                ? ($"{{\"response\":{{\"server_time\":\"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}\"}}}}", 1)
                : respond(path, body);
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            resp.Headers.TryAddWithoutValidation("x-eresult", eresult.ToString());
            return resp;
        }
    }

    private static readonly string Secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(20));
    private static readonly string Identity = Convert.ToBase64String(RandomNumberGenerator.GetBytes(20));

    private static (AuthenticatorLinker linker, FakeApi fake) Create(Func<string, string, (string, int)> respond)
    {
        var fake = new FakeApi(respond);
        var http = new SteamHttp(fake);
        var session = TestData.Account().Session!;
        return (new AuthenticatorLinker(http, new SteamTime(http), session), fake);
    }

    [Fact]
    public async Task Transfer_Success_ReturnsNewSecretsWithDeviceAndSession()
    {
        var (linker, fake) = Create((path, body) => path switch
        {
            "/ITwoFactorService/RemoveAuthenticatorViaChallengeStart/v1/" => ("""{"response":{}}""", 1),
            "/ITwoFactorService/RemoveAuthenticatorViaChallengeContinue/v1/" => (
                "{\"response\":{\"success\":true,\"replacement_token\":{\"shared_secret\":\"" + Secret + "\",\"identity_secret\":\"" + Identity + "\"," +
                "\"serial_number\":\"123\",\"revocation_code\":\"R99999\",\"uri\":\"otpauth://x\",\"server_time\":\"1731900000\",\"account_name\":\"moved\"," +
                "\"token_gid\":\"g\",\"secret_1\":\"c2VjcmV0\",\"steamguard_scheme\":2}}}", 1),
            _ => ("{}", 1),
        });

        Assert.Null(await linker.TransferStartAsync());
        Assert.Equal(AuthenticatorLinker.TransferResult.Success, await linker.TransferFinishAsync(" 12345 "));

        var acc = linker.LinkedAccount!;
        Assert.Equal(Secret, acc.SharedSecret);
        Assert.Equal(Identity, acc.IdentitySecret);
        Assert.Equal("R99999", acc.RevocationCode);
        Assert.Equal("moved", acc.AccountName);
        Assert.StartsWith("android:", acc.DeviceId);
        Assert.True(acc.FullyEnrolled);
        Assert.NotEqual(0UL, acc.SteamId);

        var cont = fake.Calls.Single(c => c.Path.EndsWith("ViaChallengeContinue/v1/", StringComparison.Ordinal));
        Assert.Contains("sms_code=12345", cont.Body);
        Assert.Contains("generate_new_token=true", cont.Body);
        Assert.Contains("version=2", cont.Body);
        Assert.Contains("access_token=", cont.Query);
        // The generated code must be valid for the new secret, i.e. the account is usable straight away.
        Assert.Matches("^[23456789BCDFGHJKMNPQRTVWXY]{5}$", Crypto.SteamGuardCode.Generate(acc.SharedSecret!, 1731900000));
    }

    [Fact]
    public async Task Transfer_WrongSmsCode_IsBadCode_AndNothingIsReturned()
    {
        var (linker, _) = Create((path, _) => path.EndsWith("ViaChallengeContinue/v1/", StringComparison.Ordinal)
            ? ("""{"response":{}}""", 89) : ("""{"response":{}}""", 1));
        Assert.Null(await linker.TransferStartAsync());
        Assert.Equal(AuthenticatorLinker.TransferResult.BadCode, await linker.TransferFinishAsync("00000"));
        Assert.Null(linker.LinkedAccount);
    }

    [Fact]
    public async Task TransferStart_Refused_ExplainsWhy()
    {
        var (linker, _) = Create((_, _) => ("""{"response":{}}""", 2));
        var problem = await linker.TransferStartAsync();
        Assert.NotNull(problem);
        Assert.Contains("phone number", problem);

        var (limited, _) = Create((_, _) => ("""{"response":{}}""", 84));
        Assert.Contains("rate-limiting", await limited.TransferStartAsync());
    }

    [Fact]
    public async Task SteamHttp_ReadsEResultHeader()
    {
        var http = new SteamHttp(new FakeApi((_, _) => ("{}", 29)));
        var r = await http.PostFormWithResultAsync(new Uri("https://api.steampowered.com/X/Y/v1/"), []);
        Assert.Equal(29, r.EResult);
        Assert.False(r.IsOk);
    }
}
