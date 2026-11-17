using System.Collections.Concurrent;
using System.Text.Json;
using Arcline.SteamGuard.Models;

namespace Arcline.SteamGuard.Steam;

/// <summary>
/// Keeps web sessions alive. Access tokens (~24 h) are refreshed from the refresh token, and
/// the refresh token itself is rotated when Steam allows it (renewal_type=1), so an account does
/// not silently expire after ~200 days. Every change is persisted through <see cref="IAccountStateStore"/>
/// (the old SDA refreshed tokens in memory only and lost them on restart).
/// </summary>
public sealed class SessionService(SteamHttp http, IAccountStateStore store)
{
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _locks = new();

    /// <summary>Refreshes the access token if it is missing or about to expire.</summary>
    public async Task EnsureFreshAsync(SteamGuardAccount account, CancellationToken ct = default)
    {
        var s = RequireSession(account);
        if (!s.IsAccessTokenExpired(TimeSpan.FromMinutes(5))) return;
        await RefreshAsync(account, ct).ConfigureAwait(false);
    }

    public async Task RefreshAsync(SteamGuardAccount account, CancellationToken ct = default)
    {
        var s = RequireSession(account);
        var gate = _locks.GetOrAdd(s.SteamId, _ => new SemaphoreSlim(1, 1));
        var tokenBefore = s.AccessToken;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Another caller refreshed while we waited.
            if (s.AccessToken != tokenBefore && !s.IsAccessTokenExpired(TimeSpan.FromMinutes(5))) return;
            if (s.IsRefreshTokenExpired()) throw new SessionExpiredException(account.DisplayName);

            string body;
            try
            {
                body = await http.PostFormAsync(SteamEndpoints.ApiMethod("IAuthenticationService", "GenerateAccessTokenForApp"),
                [
                    new("refresh_token", s.RefreshToken!),
                    new("steamid", s.SteamId.ToString()),
                    new("renewal_type", "1"),
                ], ct: ct).ConfigureAwait(false);
            }
            catch (SteamApiException ex) when (ex.HttpStatus is 401 or 403)
            {
                throw new SessionExpiredException(account.DisplayName);
            }

            string? access = null, refresh = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("response", out var r))
                {
                    if (r.TryGetProperty("access_token", out var a)) access = a.GetString();
                    if (r.TryGetProperty("refresh_token", out var rt)) refresh = rt.GetString();
                }
            }
            catch (JsonException ex) { throw new SteamApiException("Unexpected response while refreshing the session.", null, ex); }

            // An empty response means the refresh token was revoked (password change, deauthorize, etc).
            if (string.IsNullOrEmpty(access)) throw new SessionExpiredException(account.DisplayName);

            s.AccessToken = access;
            if (!string.IsNullOrEmpty(refresh)) s.RefreshToken = refresh;
            await store.SessionUpdatedAsync(account, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    internal static SessionData RequireSession(SteamGuardAccount account)
    {
        var s = account.Session;
        if (s is null || s.SteamId == 0 || string.IsNullOrEmpty(s.RefreshToken))
            throw new SessionExpiredException(account.DisplayName);
        return s;
    }
}
