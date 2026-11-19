using System.Text.Json;
using Arcline.SteamGuard.Crypto;
using Arcline.SteamGuard.Models;

namespace Arcline.SteamGuard.Steam;

/// <summary>
/// Adds a new mobile authenticator to an account (ITwoFactorService/AddAuthenticator + FinalizeAddAuthenticator).
/// Usage: AddAsync until it returns <see cref="LinkResult.AwaitingFinalization"/>, SAVE <see cref="LinkedAccount"/>
/// (it already contains the secrets and revocation code), then FinalizeAsync with the SMS / e-mail code.
/// </summary>
public sealed class AuthenticatorLinker(SteamHttp http, SteamTime time, SessionData session)
{
    public enum LinkResult { MustProvidePhoneNumber, MustConfirmEmail, AwaitingFinalization, AuthenticatorPresent, FailureAddingPhone, GeneralFailure }
    public enum FinalizeResult { Success, BadCode, UnableToGenerateCorrectCodes, GeneralFailure }

    public string DeviceId { get; } = "android:" + Guid.NewGuid();
    public string? PhoneNumber { get; set; }
    public string? PhoneCountryCode { get; set; }
    public string? ConfirmationEmailAddress { get; private set; }
    public SteamGuardAccount? LinkedAccount { get; private set; }

    /// <summary>1 = SMS, 2/3 = e-mail (Steam's confirm_type). Tells the UI where the activation code was sent.</summary>
    public int ConfirmType { get; private set; } = 1;

    public string? LastError { get; private set; }

    private bool _emailSent;

    public async Task<LinkResult> AddAsync(CancellationToken ct = default)
    {
        if (_emailSent)
        {
            var waiting = await Call("IPhoneService", "IsAccountWaitingForEmailConfirmation", [], ct).ConfigureAwait(false);
            if (Bool(waiting, "awaiting_email_confirmation")) return LinkResult.MustConfirmEmail;
            await Call("IPhoneService", "SendPhoneVerificationCode", [new("language", "0")], ct).ConfigureAwait(false);
            _emailSent = false;
        }

        var now = await time.NowAsync(ct).ConfigureAwait(false);
        var resp = await Call("ITwoFactorService", "AddAuthenticator",
        [
            new("steamid", session.SteamId.ToString()),
            new("authenticator_time", now.ToString()),
            new("authenticator_type", "1"),
            new("device_identifier", DeviceId),
            new("sms_phone_id", "1"),
        ], ct).ConfigureAwait(false);
        if (resp is null) return LinkResult.GeneralFailure;

        var status = Int(resp, "status");
        if (status == 2)
        {
            if (string.IsNullOrWhiteSpace(PhoneNumber)) return LinkResult.MustProvidePhoneNumber;
            var country = PhoneCountryCode;
            if (string.IsNullOrEmpty(country))
                country = Str(await Call("IUserAccountService", "GetUserCountry", [new("steamid", session.SteamId.ToString())], ct).ConfigureAwait(false), "country");
            var set = await Call("IPhoneService", "SetAccountPhoneNumber",
                [new("phone_number", PhoneNumber), new("phone_country_code", country ?? "")], ct).ConfigureAwait(false);
            ConfirmationEmailAddress = Str(set, "confirmation_email_address");
            if (ConfirmationEmailAddress is null) return LinkResult.FailureAddingPhone;
            _emailSent = true;
            return LinkResult.MustConfirmEmail;
        }
        if (status == 29) return LinkResult.AuthenticatorPresent;
        if (status != 1)
        {
            LastError = $"AddAuthenticator status {status}";
            return LinkResult.GeneralFailure;
        }

        var account = resp.Value.Deserialize<SteamGuardAccount>(Json.Options)!;
        account.DeviceId = DeviceId;
        account.Session = session;
        account.FullyEnrolled = false;
        if (resp.Value.TryGetProperty("confirm_type", out var ctProp) && ctProp.TryGetInt32(out var confirmType)) ConfirmType = confirmType;
        LinkedAccount = account;
        return LinkResult.AwaitingFinalization;
    }

    public async Task<FinalizeResult> FinalizeAsync(string activationCode, CancellationToken ct = default)
    {
        if (LinkedAccount?.SharedSecret is null) throw new InvalidOperationException("Call AddAsync first.");
        var t = await time.NowAsync(ct).ConfigureAwait(false);
        for (var tries = 0; tries <= 10; tries++)
        {
            var resp = await Call("ITwoFactorService", "FinalizeAddAuthenticator",
            [
                new("steamid", session.SteamId.ToString()),
                new("authenticator_code", SteamGuardCode.Generate(LinkedAccount.SharedSecret, t)),
                new("authenticator_time", t.ToString()),
                new("activation_code", activationCode.Trim()),
                new("validate_sms_code", "1"),
            ], ct).ConfigureAwait(false);
            if (resp is null) return FinalizeResult.GeneralFailure;

            var status = Int(resp, "status");
            if (status == 89) return FinalizeResult.BadCode;
            if (status == 88)
            {
                // Code mismatch: our clock is off. Resync and try again.
                if (tries >= 10) return FinalizeResult.UnableToGenerateCorrectCodes;
                await time.SyncAsync(force: true, ct).ConfigureAwait(false);
                t = time.Now;
                continue;
            }
            if (!Bool(resp, "success"))
            {
                LastError = $"FinalizeAddAuthenticator status {status}";
                return FinalizeResult.GeneralFailure;
            }
            if (Bool(resp, "want_more"))
            {
                t += SteamGuardCode.PeriodSeconds; // Steam wants codes from consecutive windows.
                continue;
            }
            LinkedAccount.FullyEnrolled = true;
            return FinalizeResult.Success;
        }
        return FinalizeResult.GeneralFailure;
    }

    // ---------- moving an existing authenticator (e.g. from the Steam app) to this app ----------

    public enum TransferResult { Success, BadCode, Failed }

    /// <summary>
    /// Starts moving the account's existing mobile authenticator here: Steam texts a code to the phone on the account.
    /// This is the same flow as "use this device" in the official app. Needs a phone number on the account.
    /// Returns null on success, otherwise a user-facing reason.
    /// </summary>
    public async Task<string?> TransferStartAsync(CancellationToken ct = default)
    {
        var r = await CallWithResult("ITwoFactorService", "RemoveAuthenticatorViaChallengeStart", [], ct).ConfigureAwait(false);
        if (r.IsOk) return null;
        LastError = $"RemoveAuthenticatorViaChallengeStart EResult {r.EResult}";
        return r.EResult switch
        {
            84 or 87 => "Steam is rate-limiting this request. Wait a while (up to an hour) and try again.",
            _ => $"Steam refused to start moving the authenticator (EResult {r.EResult}). It requires a phone number on the account; " +
                 "if there is none, remove the old authenticator with its revocation code and add a new one instead.",
        };
    }

    /// <summary>
    /// Finishes the move with the SMS code. On success <see cref="LinkedAccount"/> holds NEW secrets and the old
    /// authenticator stops working immediately, so save the account right away.
    /// </summary>
    public async Task<TransferResult> TransferFinishAsync(string smsCode, CancellationToken ct = default)
    {
        var r = await CallWithResult("ITwoFactorService", "RemoveAuthenticatorViaChallengeContinue",
        [
            new("sms_code", smsCode.Trim()),
            new("generate_new_token", "true"),
            new("version", "2"),
        ], ct).ConfigureAwait(false);

        JsonElement? token = null;
        try
        {
            using var doc = JsonDocument.Parse(r.Body);
            if (doc.RootElement.TryGetProperty("response", out var resp) && resp.TryGetProperty("replacement_token", out var t))
                token = t.Clone();
        }
        catch (JsonException) { }

        if (!r.IsOk || token is null)
        {
            LastError = $"RemoveAuthenticatorViaChallengeContinue EResult {r.EResult}";
            // 2 = Fail, 65 = InvalidLoginAuthCode, 88/89 = code mismatch: all mean "that SMS code did not work".
            return r.EResult is 2 or 65 or 88 or 89 ? TransferResult.BadCode : TransferResult.Failed;
        }

        var account = token.Value.Deserialize<SteamGuardAccount>(Json.Options)
                      ?? throw new SteamApiException("Steam returned an empty replacement authenticator.");
        if (string.IsNullOrEmpty(account.SharedSecret) || string.IsNullOrEmpty(account.IdentitySecret))
            throw new SteamApiException("Steam's replacement authenticator is missing its secrets.");
        account.DeviceId = DeviceId;
        account.Session = session;
        account.FullyEnrolled = true;
        LinkedAccount = account;
        return TransferResult.Success;
    }

    /// <summary>Removes the authenticator. scheme 1 = back to e-mail codes, 2 = no Steam Guard.</summary>
    public static async Task<bool> RemoveAsync(SteamHttp http, SteamGuardAccount account, int scheme, CancellationToken ct = default)
    {
        var s = SessionService.RequireSession(account);
        var body = await http.PostFormAsync(
            new Uri(SteamEndpoints.ApiMethod("ITwoFactorService", "RemoveAuthenticator") + "?access_token=" + Uri.EscapeDataString(s.AccessToken ?? "")),
            [new("revocation_code", account.RevocationCode ?? ""), new("revocation_reason", "1"), new("steamguard_scheme", scheme.ToString())],
            ct: ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("response", out var r) && r.TryGetProperty("success", out var ok) && ok.GetBoolean();
    }

    private Task<SteamResponse> CallWithResult(string iface, string method, List<KeyValuePair<string, string>> form, CancellationToken ct) =>
        http.PostFormWithResultAsync(new Uri(SteamEndpoints.ApiMethod(iface, method) + "?access_token=" + Uri.EscapeDataString(session.AccessToken ?? "")), form, ct: ct);

    private async Task<JsonElement?> Call(string iface, string method, List<KeyValuePair<string, string>> form, CancellationToken ct)
    {
        var body = (await CallWithResult(iface, method, form, ct).ConfigureAwait(false)).Body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("response", out var r) ? r.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    private static int Int(JsonElement? e, string name) =>
        e is { } v && v.TryGetProperty(name, out var p) ? p.ValueKind == JsonValueKind.String ? int.TryParse(p.GetString(), out var i) ? i : 0 : p.TryGetInt32(out var n) ? n : 0 : 0;

    private static bool Bool(JsonElement? e, string name) =>
        e is { } v && v.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    private static string? Str(JsonElement? e, string name) =>
        e is { } v && v.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
