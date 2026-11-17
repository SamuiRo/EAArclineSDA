using Arcline.SteamGuard.Crypto;
using Arcline.SteamGuard.Models;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;

namespace Arcline.SteamGuard.Steam;

/// <summary>Interactive bits of a Steam login that only the user can answer.</summary>
public interface ILoginPrompts
{
    /// <summary>Code from the e-mail Steam just sent. Return null to cancel.</summary>
    Task<string?> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect);

    /// <summary>
    /// Steam Guard code from another authenticator (only asked when we do not hold the shared_secret,
    /// e.g. when the account is protected by the official mobile app). Return null to cancel.
    /// </summary>
    Task<string?> GetDeviceCodeAsync(bool previousCodeWasIncorrect);
}

/// <summary>
/// Logs in through SteamKit2's new authentication flow (MobileApp platform), producing a
/// <see cref="SessionData"/> with access + refresh tokens usable for mobileconf and ITwoFactorService.
/// </summary>
public sealed class SteamLoginService(SteamTime time)
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    public async Task<SessionData> LoginAsync(string username, string password, ILoginPrompts prompts,
        SteamGuardAccount? authenticator = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrEmpty(password);

        var client = new SteamClient();
        var manager = new CallbackManager(client);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var s1 = manager.Subscribe<SteamClient.ConnectedCallback>(_ => connected.TrySetResult());
        using var s2 = manager.Subscribe<SteamClient.DisconnectedCallback>(_ =>
            connected.TrySetException(new SteamApiException("Could not connect to Steam.")));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pump = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                await manager.RunWaitCallbackAsync(cts.Token).ConfigureAwait(false);
            }
        }, CancellationToken.None);

        try
        {
            client.Connect();
            await connected.Task.WaitAsync(ConnectTimeout, ct).ConfigureAwait(false);

            var session = await client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
            {
                Username = username.Trim(),
                Password = password,
                IsPersistentSession = true,
                PlatformType = EAuthTokenPlatformType.k_EAuthTokenPlatformType_MobileApp,
                ClientOSType = EOSType.Android9,
                DeviceFriendlyName = "Arcline SDA",
                Authenticator = new Authenticator(authenticator, prompts, time),
            }).ConfigureAwait(false);

            var result = await session.PollingWaitForResultAsync(ct).ConfigureAwait(false);
            return new SessionData
            {
                SteamId = session.SteamID.ConvertToUInt64(),
                AccessToken = result.AccessToken,
                RefreshToken = result.RefreshToken,
            };
        }
        catch (AuthenticationException ex)
        {
            throw new SteamGuardException(ex.Result switch
            {
                EResult.InvalidPassword => "Wrong username or password.",
                EResult.RateLimitExceeded or EResult.AccountLoginDeniedThrottle => "Too many login attempts. Wait a while and try again.",
                EResult.TwoFactorCodeMismatch => "Steam Guard code was rejected.",
                _ => $"Steam login failed: {ex.Result}",
            }, ex);
        }
        catch (TimeoutException ex)
        {
            throw new SteamApiException("Timed out connecting to Steam.", null, ex);
        }
        finally
        {
            client.Disconnect();
            await cts.CancelAsync().ConfigureAwait(false);
            try { await pump.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private sealed class Authenticator(SteamGuardAccount? account, ILoginPrompts prompts, SteamTime time) : IAuthenticator
    {
        private int _attempts;

        public Task<bool> AcceptDeviceConfirmationAsync() => Task.FromResult(false);

        public async Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
        {
            if (account?.SharedSecret is { Length: > 0 } secret)
            {
                if (previousCodeWasIncorrect)
                {
                    if (++_attempts >= 3)
                        throw new SteamGuardException("Steam keeps rejecting the generated codes. Is this maFile still the active authenticator for the account?");
                    await time.SyncAsync(force: true).ConfigureAwait(false);
                    // Wait for the next 30 s window instead of a blind 30 s sleep.
                    await Task.Delay(TimeSpan.FromSeconds(SteamGuardCode.SecondsRemaining(time.Now) + 1)).ConfigureAwait(false);
                }
                return SteamGuardCode.Generate(secret, await time.NowAsync().ConfigureAwait(false));
            }
            return await prompts.GetDeviceCodeAsync(previousCodeWasIncorrect).ConfigureAwait(false)
                   ?? throw new OperationCanceledException("Login cancelled.");
        }

        public async Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) =>
            await prompts.GetEmailCodeAsync(email, previousCodeWasIncorrect).ConfigureAwait(false)
            ?? throw new OperationCanceledException("Login cancelled.");
    }
}
