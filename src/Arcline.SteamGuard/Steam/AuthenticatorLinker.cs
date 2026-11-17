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

    private async Task<JsonElement?> Call(string iface, string method, List<KeyValuePair<string, string>> form, CancellationToken ct)
    {
        var uri = new Uri(SteamEndpoints.ApiMethod(iface, method) + "?access_token=" + Uri.EscapeDataString(session.AccessToken ?? ""));
        var body = await http.PostFormAsync(uri, form, ct: ct).ConfigureAwait(false);
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
