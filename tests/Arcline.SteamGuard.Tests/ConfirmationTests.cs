using System.Net;
using System.Text;
using Arcline.SteamGuard.Models;
using Arcline.SteamGuard.Steam;

namespace Arcline.SteamGuard.Tests;

/// <summary>Covers the SDA issue #1111 fix (trade protection acknowledgement) with a fake Steam.</summary>
public class ConfirmationTests
{
    private sealed class FakeSteam : HttpMessageHandler
    {
        public List<(string Method, string Path, string Body, string Cookie)> Calls { get; } = [];
        public bool Acknowledged { get; set; }
        public bool RequireAck { get; set; } = true;
        public bool ListNeedsAuthOnce { get; set; }
        public int Refreshes { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var cookie = request.Headers.TryGetValues("Cookie", out var c) ? string.Join(";", c) : "";
            var path = request.RequestUri!.AbsolutePath;
            Calls.Add((request.Method.Method, path, body, cookie));

            string json = path switch
            {
                "/ITwoFactorService/QueryTime/v1/" => $"{{\"response\":{{\"server_time\":\"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}\"}}}}",
                "/IAuthenticationService/GenerateAccessTokenForApp/v1/" => Refresh(),
                "/mobileconf/getlist" => List(),
                "/trade/new/acknowledge" => Ack(body),
                "/mobileconf/multiajaxop" or "/mobileconf/ajaxop" => $"{{\"success\":{(!RequireAck || Acknowledged ? "true" : "false")}}}",
                _ => "{}",
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        private string Refresh()
        {
            Refreshes++;
            return $"{{\"response\":{{\"access_token\":\"{TestData.FakeJwt(DateTimeOffset.UtcNow.AddHours(24))}\"}}}}";
        }

        private string List()
        {
            if (ListNeedsAuthOnce) { ListNeedsAuthOnce = false; return "{\"success\":false,\"needauth\":true}"; }
            return """{"success":true,"conf":[{"type":2,"type_name":"Trade Offer","id":"111","creator_id":"999","nonce":"222","creation_time":1731840000,"headline":"Trade with X","summary":["You will give 1 item"],"accept":"Send Offer","cancel":"Cancel"}]}""";
        }

        private string Ack(string body)
        {
            Assert.Contains("message=1", body);
            Assert.Contains("sessionid=", body);
            Acknowledged = true;
            return "{\"success\":1}";
        }
    }

    private static (ConfirmationService svc, FakeSteam fake, InMemoryAccountStateStore store) Create()
    {
        var fake = new FakeSteam();
        var http = new SteamHttp(fake);
        var store = new InMemoryAccountStateStore();
        var time = new SteamTime(http);
        var sessions = new SessionService(http, store);
        return (new ConfirmationService(http, time, sessions, store), fake, store);
    }

    [Fact]
    public async Task List_ParsesConfirmations_AndSendsSessionCookies()
    {
        var (svc, fake, _) = Create();
        var acc = TestData.Account();
        var list = await svc.GetAsync(acc);
        var c = Assert.Single(list);
        Assert.Equal(ConfirmationType.Trade, c.Type);
        Assert.Equal(111UL, c.Id);
        Assert.Equal(222UL, c.Nonce);
        var call = fake.Calls.Single(x => x.Path == "/mobileconf/getlist");
        Assert.Contains("steamLoginSecure=" + acc.SteamId + "%7C%7C", call.Cookie);
        Assert.Contains("sessionid=" + acc.Session!.SessionId, call.Cookie);
    }

    [Fact]
    public async Task AcceptTrade_AcknowledgesTradeProtectionFirst_AndRemembersIt()
    {
        var (svc, fake, store) = Create();
        var acc = TestData.Account();
        var confs = await svc.GetAsync(acc);

        var res = await svc.AcceptAsync(acc, confs);
        Assert.All(res, r => Assert.True(r.Success));
        Assert.True(store.IsTradeProtectionAcknowledged(acc.SteamId));
        var ackIndex = fake.Calls.FindIndex(x => x.Path == "/trade/new/acknowledge");
        var opIndex = fake.Calls.FindIndex(x => x.Path == "/mobileconf/multiajaxop");
        Assert.True(ackIndex >= 0 && ackIndex < opIndex, "acknowledge must happen before the confirmation");

        // Second accept: no second acknowledge request.
        await svc.AcceptAsync(acc, confs);
        Assert.Equal(1, fake.Calls.Count(x => x.Path == "/trade/new/acknowledge"));
    }

    [Fact]
    public async Task AcceptFailure_TriggersAcknowledgeAndRetry_EvenIfFlagWasStale()
    {
        var (svc, fake, store) = Create();
        var acc = TestData.Account();
        await store.SetTradeProtectionAcknowledgedAsync(acc.SteamId, true, default); // stale flag: Steam still wants the ack
        var confs = await svc.GetAsync(acc);

        var res = await svc.AcceptAsync(acc, confs);
        Assert.All(res, r => Assert.True(r.Success));
        Assert.Equal(2, fake.Calls.Count(x => x.Path == "/mobileconf/multiajaxop"));
        Assert.Equal(1, fake.Calls.Count(x => x.Path == "/trade/new/acknowledge"));
    }

    [Fact]
    public async Task Deny_NeverAcknowledges()
    {
        var (svc, fake, _) = Create();
        fake.RequireAck = false;
        var acc = TestData.Account();
        var res = await svc.DenyAsync(acc, await svc.GetAsync(acc));
        Assert.All(res, r => Assert.True(r.Success));
        Assert.DoesNotContain(fake.Calls, x => x.Path == "/trade/new/acknowledge");
        Assert.Contains("op=cancel", fake.Calls.Single(x => x.Path == "/mobileconf/multiajaxop").Body);
    }

    [Fact]
    public async Task ExpiredAccessToken_IsRefreshed_BeforeRequest()
    {
        var (svc, fake, _) = Create();
        var acc = TestData.Account();
        acc.Session!.AccessToken = TestData.FakeJwt(DateTimeOffset.UtcNow.AddMinutes(-1));
        await svc.GetAsync(acc);
        Assert.Equal(1, fake.Refreshes);
        Assert.False(acc.Session.IsAccessTokenExpired());
    }

    [Fact]
    public async Task NeedAuth_ForcesOneRefreshAndRetry()
    {
        var (svc, fake, _) = Create();
        fake.ListNeedsAuthOnce = true;
        var list = await svc.GetAsync(TestData.Account());
        Assert.Single(list);
        Assert.Equal(1, fake.Refreshes);
    }

    [Fact]
    public async Task ExpiredRefreshToken_Throws_SessionExpired()
    {
        var (svc, _, _) = Create();
        var acc = TestData.Account();
        acc.Session!.AccessToken = TestData.FakeJwt(DateTimeOffset.UtcNow.AddMinutes(-1));
        acc.Session.RefreshToken = TestData.FakeJwt(DateTimeOffset.UtcNow.AddMinutes(-1));
        await Assert.ThrowsAsync<SessionExpiredException>(() => svc.GetAsync(acc));
    }
}
