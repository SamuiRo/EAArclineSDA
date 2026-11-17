using System.Net;
using System.Net.Http.Headers;
using Arcline.SteamGuard.Models;

namespace Arcline.SteamGuard.Steam;

public static class SteamEndpoints
{
    public static readonly Uri Community = new("https://steamcommunity.com");
    public static readonly Uri Api = new("https://api.steampowered.com");

    public static Uri ApiMethod(string iface, string method, int version = 1) =>
        new(Api, $"/{iface}/{method}/v{version}/");
}

/// <summary>
/// One pooled HttpClient for all Steam traffic. Cookies are sent per request from the
/// account's <see cref="SessionData"/> (no shared cookie jar, so accounts never leak into each other).
/// Transient failures (network errors, timeouts, 429, 5xx) are retried with exponential backoff.
/// </summary>
public sealed class SteamHttp : IDisposable
{
    public const string MobileUserAgent = "okhttp/4.9.2";
    public const string MobileClientVersion = "777777 3.10.3";
    private const int MaxAttempts = 3;

    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public SteamHttp(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = timeout ?? TimeSpan.FromSeconds(20) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(MobileUserAgent);
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        _ownsClient = true;
    }

    public static SteamHttp Shared { get; } = new();

    public Task<string> GetAsync(Uri uri, SessionData? session = null, CancellationToken ct = default) =>
        SendAsync(() => Build(HttpMethod.Get, uri, session, null, null), ct);

    public Task<string> PostFormAsync(Uri uri, IEnumerable<KeyValuePair<string, string>> form, SessionData? session = null,
        Uri? referer = null, CancellationToken ct = default)
    {
        var fields = form.ToList();
        return SendAsync(() => Build(HttpMethod.Post, uri, session, fields, referer), ct);
    }

    private static HttpRequestMessage Build(HttpMethod method, Uri uri, SessionData? session,
        List<KeyValuePair<string, string>>? form, Uri? referer)
    {
        var req = new HttpRequestMessage(method, uri);
        if (form is not null) req.Content = new FormUrlEncodedContent(form);
        if (session is not null && uri.Host.EndsWith("steamcommunity.com", StringComparison.OrdinalIgnoreCase))
        {
            req.Headers.TryAddWithoutValidation("Cookie", BuildCookieHeader(session));
            if (method == HttpMethod.Post)
            {
                req.Headers.TryAddWithoutValidation("Origin", SteamEndpoints.Community.ToString().TrimEnd('/'));
                req.Headers.Referrer = referer ?? SteamEndpoints.Community;
            }
            req.Headers.TryAddWithoutValidation("X-Requested-With", "com.valvesoftware.android.steam.community");
        }
        return req;
    }

    internal static string BuildCookieHeader(SessionData s)
    {
        SessionIds.Ensure(s);
        var steamLoginSecure = Uri.EscapeDataString($"{s.SteamId}||{s.AccessToken}");
        return string.Join("; ",
            $"steamLoginSecure={steamLoginSecure}",
            $"sessionid={s.SessionId}",
            "mobileClient=android",
            $"mobileClientVersion={Uri.EscapeDataString(MobileClientVersion)}",
            "Steam_Language=english");
    }

    private async Task<string> SendAsync(Func<HttpRequestMessage> factory, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var req = factory();
            try
            {
                using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                var status = (int)resp.StatusCode;
                if (status is >= 300 and < 400)
                {
                    // steamcommunity redirects to the login page when the session is not valid.
                    throw new SteamApiException($"Steam redirected {req.RequestUri?.AbsolutePath} to {resp.Headers.Location} (session is not authorized).", status);
                }
                if ((status == 429 || status >= 500) && attempt < MaxAttempts)
                {
                    await Delay(attempt, resp.Headers.RetryAfter?.Delta, ct).ConfigureAwait(false);
                    continue;
                }
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw new SteamApiException($"Steam returned HTTP {status} for {req.RequestUri?.AbsolutePath}.", status);
                return body;
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts)
            {
                _ = ex;
                await Delay(attempt, null, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < MaxAttempts)
            {
                await Delay(attempt, null, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new SteamApiException($"Network error talking to Steam: {ex.Message}", null, ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new SteamApiException("Steam did not respond in time.", null, ex);
            }
        }
    }

    private static Task Delay(int attempt, TimeSpan? retryAfter, CancellationToken ct)
    {
        var baseDelay = retryAfter ?? TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt));
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));
        var delay = baseDelay + jitter;
        if (delay > TimeSpan.FromSeconds(30)) delay = TimeSpan.FromSeconds(30);
        return Task.Delay(delay, ct);
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }
}

internal static class SessionIds
{
    /// <summary>Steam accepts any 24-hex-digit sessionid as long as the cookie and the form field match.</summary>
    public static string Ensure(SessionData s)
    {
        if (string.IsNullOrEmpty(s.SessionId))
            s.SessionId = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
        return s.SessionId;
    }
}
