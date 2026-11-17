using System.Globalization;
using System.Text.Json;
using Arcline.SteamGuard.Crypto;
using Arcline.SteamGuard.Models;

namespace Arcline.SteamGuard.Steam;

public sealed record ConfirmationOutcome(ulong ConfirmationId, bool Success, string? Message = null);

/// <summary>
/// Mobile confirmations (trades, market listings, purchases, API keys...).
///
/// Trade protection fix (SDA issue #1111): since July 2025 Steam refuses to confirm trades until the
/// account has acknowledged the "trade protection" notice that the web UI shows once. The old SDA
/// ignored the <c>success:false</c> it got back, so "Confirm" silently did nothing. Here the notice is
/// acknowledged automatically (POST /trade/new/acknowledge, the same request the web popup sends) before
/// the first trade confirmation, and again followed by one retry if Steam still rejects a confirmation.
/// The acknowledged state is persisted per account.
/// </summary>
public sealed class ConfirmationService(SteamHttp http, SteamTime time, SessionService sessions, IAccountStateStore store)
{
    private const string TagList = "conf";
    private const string TagAccept = "accept";
    private const string TagReject = "reject";

    public async Task<IReadOnlyList<Confirmation>> GetAsync(SteamGuardAccount account, CancellationToken ct = default)
    {
        await sessions.EnsureFreshAsync(account, ct).ConfigureAwait(false);
        var resp = await FetchListAsync(account, ct).ConfigureAwait(false);
        if (resp.NeedAuth)
        {
            // Access token was rejected although it looked valid: force a refresh once.
            await sessions.RefreshAsync(account, ct).ConfigureAwait(false);
            resp = await FetchListAsync(account, ct).ConfigureAwait(false);
            if (resp.NeedAuth) throw new SessionExpiredException(account.DisplayName);
        }
        if (!resp.Success)
            throw new SteamApiException(string.IsNullOrWhiteSpace(resp.Message)
                ? "Steam refused to list confirmations (is this authenticator still active on the account?)."
                : resp.Message + (string.IsNullOrWhiteSpace(resp.Detail) ? "" : " " + resp.Detail));
        return resp.Confirmations ?? [];
    }

    public Task<IReadOnlyList<ConfirmationOutcome>> AcceptAsync(SteamGuardAccount account, IReadOnlyCollection<Confirmation> confirmations, CancellationToken ct = default)
        => RespondAsync(account, confirmations, accept: true, ct);

    public Task<IReadOnlyList<ConfirmationOutcome>> DenyAsync(SteamGuardAccount account, IReadOnlyCollection<Confirmation> confirmations, CancellationToken ct = default)
        => RespondAsync(account, confirmations, accept: false, ct);

    public async Task<IReadOnlyList<ConfirmationOutcome>> RespondAsync(SteamGuardAccount account,
        IReadOnlyCollection<Confirmation> confirmations, bool accept, CancellationToken ct = default)
    {
        if (confirmations.Count == 0) return [];
        await sessions.EnsureFreshAsync(account, ct).ConfigureAwait(false);

        var acknowledgedNow = false;
        if (accept && confirmations.Any(c => c.Type == ConfirmationType.Trade) && !store.IsTradeProtectionAcknowledged(account.SteamId))
        {
            // Failure here is not fatal: the notice may already have been accepted in a browser.
            acknowledgedNow = await TryAcknowledgeTradeProtectionAsync(account, ct).ConfigureAwait(false);
        }

        var ok = await SendBatchAsync(account, confirmations, accept, ct).ConfigureAwait(false);
        if (ok) return confirmations.Select(c => new ConfirmationOutcome(c.Id, true)).ToList();

        if (accept && !acknowledgedNow)
        {
            // Steam said no. The most common cause since 2025-07 is the unacknowledged trade protection notice.
            if (await TryAcknowledgeTradeProtectionAsync(account, ct).ConfigureAwait(false)
                && await SendBatchAsync(account, confirmations, accept, ct).ConfigureAwait(false))
                return confirmations.Select(c => new ConfirmationOutcome(c.Id, true)).ToList();
        }

        // Fall back to one-by-one so the caller learns exactly which confirmation failed.
        var results = new List<ConfirmationOutcome>(confirmations.Count);
        foreach (var c in confirmations)
        {
            var single = await SendSingleAsync(account, c, accept, ct).ConfigureAwait(false);
            results.Add(new ConfirmationOutcome(c.Id, single.Success, single.Success ? null :
                single.Message ?? "Steam rejected the confirmation. It may have expired or already been handled; refresh the list."));
        }
        return results;
    }

    /// <summary>
    /// Accepts Steam's one-time "trade protection" notice for the account (what the web popup does).
    /// Returns true when Steam accepted the request; the state is persisted.
    /// </summary>
    public async Task<bool> AcknowledgeTradeProtectionAsync(SteamGuardAccount account, CancellationToken ct = default)
    {
        await sessions.EnsureFreshAsync(account, ct).ConfigureAwait(false);
        var s = SessionService.RequireSession(account);
        var sid = SessionIds.Ensure(s);
        await http.PostFormAsync(new Uri(SteamEndpoints.Community, "/trade/new/acknowledge"),
            [new("sessionid", sid), new("message", "1")],
            s, referer: new Uri(SteamEndpoints.Community, "/tradeoffer/new/"), ct: ct).ConfigureAwait(false);
        await store.SetTradeProtectionAcknowledgedAsync(account.SteamId, true, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> TryAcknowledgeTradeProtectionAsync(SteamGuardAccount account, CancellationToken ct)
    {
        try { return await AcknowledgeTradeProtectionAsync(account, ct).ConfigureAwait(false); }
        catch (SteamApiException) { return false; }
    }

    private async Task<ConfirmationListResponse> FetchListAsync(SteamGuardAccount account, CancellationToken ct)
    {
        var q = await QueryAsync(account, TagList, ct).ConfigureAwait(false);
        var uri = new Uri(SteamEndpoints.Community, "/mobileconf/getlist?" + ToQuery(q));
        string body;
        try { body = await http.GetAsync(uri, account.Session, ct).ConfigureAwait(false); }
        catch (SteamApiException ex) when (ex.HttpStatus is >= 300 and < 400 or 401 or 403)
        {
            return new ConfirmationListResponse { NeedAuth = true };
        }
        return Deserialize<ConfirmationListResponse>(body, "confirmation list");
    }

    private async Task<bool> SendBatchAsync(SteamGuardAccount account, IReadOnlyCollection<Confirmation> confs, bool accept, CancellationToken ct)
    {
        var form = await QueryAsync(account, accept ? TagAccept : TagReject, ct).ConfigureAwait(false);
        form.Insert(0, new("op", accept ? "allow" : "cancel"));
        form.Add(new("sessionid", SessionIds.Ensure(account.Session!)));
        foreach (var c in confs)
        {
            form.Add(new("cid[]", c.Id.ToString(CultureInfo.InvariantCulture)));
            form.Add(new("ck[]", c.Nonce.ToString(CultureInfo.InvariantCulture)));
        }
        try
        {
            var body = await http.PostFormAsync(new Uri(SteamEndpoints.Community, "/mobileconf/multiajaxop"), form, account.Session,
                referer: new Uri(SteamEndpoints.Community, "/mobileconf/conf"), ct: ct).ConfigureAwait(false);
            return Deserialize<SuccessResponse>(body, "confirmation response").Success;
        }
        catch (SteamApiException) { return false; }
    }

    private async Task<SuccessResponse> SendSingleAsync(SteamGuardAccount account, Confirmation c, bool accept, CancellationToken ct)
    {
        var q = await QueryAsync(account, accept ? TagAccept : TagReject, ct).ConfigureAwait(false);
        q.Insert(0, new("op", accept ? "allow" : "cancel"));
        q.Add(new("cid", c.Id.ToString(CultureInfo.InvariantCulture)));
        q.Add(new("ck", c.Nonce.ToString(CultureInfo.InvariantCulture)));
        try
        {
            var body = await http.GetAsync(new Uri(SteamEndpoints.Community, "/mobileconf/ajaxop?" + ToQuery(q)), account.Session, ct).ConfigureAwait(false);
            return Deserialize<SuccessResponse>(body, "confirmation response");
        }
        catch (SteamApiException ex) { return new SuccessResponse { Success = false, Message = ex.Message }; }
    }

    private async Task<List<KeyValuePair<string, string>>> QueryAsync(SteamGuardAccount account, string tag, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(account.IdentitySecret)) throw new SteamGuardException("identity_secret is missing in the maFile; confirmations are not possible.");
        if (string.IsNullOrEmpty(account.DeviceId)) throw new SteamGuardException("device_id is missing in the maFile; confirmations are not possible.");
        var t = await time.NowAsync(ct).ConfigureAwait(false);
        return
        [
            new("p", account.DeviceId),
            new("a", account.SteamId.ToString(CultureInfo.InvariantCulture)),
            new("k", ConfirmationSigner.Sign(account.IdentitySecret, t, tag)),
            new("t", t.ToString(CultureInfo.InvariantCulture)),
            new("m", "react"),
            new("tag", tag),
        ];
    }

    private static string ToQuery(IEnumerable<KeyValuePair<string, string>> q) =>
        string.Join("&", q.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));

    private static T Deserialize<T>(string body, string what) where T : new()
    {
        try { return JsonSerializer.Deserialize<T>(body, Json.Options) ?? new T(); }
        catch (JsonException ex) { throw new SteamApiException($"Steam returned an unexpected {what} (not JSON).", null, ex); }
    }
}
