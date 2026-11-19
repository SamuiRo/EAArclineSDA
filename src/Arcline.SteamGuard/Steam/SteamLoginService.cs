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
    /// Steam Guard code from another authenticator. Only asked when we do not hold the shared_secret,
    /// which means the account is already protected by a mobile authenticator (e.g. the Steam app).
    /// Return null to cancel.
    /// </summary>
    Task<string?> GetDeviceCodeAsync(bool previousCodeWasIncorrect);
}

/// <summary>
/// Logs in through Steam's authentication service over a SteamKit2 CM connection (MobileApp platform).
/// It produces a <see cref="SessionData"/> with access + refresh tokens usable for mobileconf and ITwoFactorService.
///
/// Steam drops idle, not-yet-logged-on CM connections after a while. That used to happen while the user was
/// fetching the e-mail code: submitting the code then failed with AsyncJobFailedException and crashed the app.
/// The auth session lives server-side, so now:
///   * after the user types a code, the connection is re-established before the code is sent;
///   * if sending still fails because the connection dropped, the same code is re-sent on a fresh connection
///     and the session is polled here.
/// </summary>
public sealed class SteamLoginService(SteamTime time)
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMinutes(3);

    public async Task<SessionData> LoginAsync(string username, string password, ILoginPrompts prompts,
        SteamGuardAccount? authenticator = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrEmpty(password);

        var conn = new Connection(ct);
        try
        {
            var guard = new Authenticator(authenticator, prompts, time, conn, ct);
            var session = await conn.CallAsync(() => conn.Client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
            {
                Username = username.Trim(),
                Password = password,
                IsPersistentSession = true,
                PlatformType = EAuthTokenPlatformType.k_EAuthTokenPlatformType_MobileApp,
                ClientOSType = EOSType.Android9,
                DeviceFriendlyName = "Arcline SDA",
                Authenticator = guard,
            })).ConfigureAwait(false);

            AuthPollResult result;
            try
            {
                result = await session.PollingWaitForResultAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsConnectionLoss(ex, ct) && guard.LastCode is { } code)
            {
                // The code was entered but the connection died while sending it: resend on a fresh connection.
                await conn.CallAsync(async () => { await session.SendSteamGuardCodeAsync(code.Value, code.Type).ConfigureAwait(false); return true; }).ConfigureAwait(false);
                result = await PollAsync(session, conn, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsConnectionLoss(ex, ct))
            {
                result = await PollAsync(session, conn, ct).ConfigureAwait(false);
            }

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
                EResult.TwoFactorCodeMismatch or EResult.InvalidLoginAuthCode => "The Steam Guard code was rejected.",
                EResult.ExpiredLoginAuthCode or EResult.Expired or EResult.FileNotFound => "The login attempt expired. Start the login again.",
                _ => $"Steam login failed: {ex.Result}",
            }, ex);
        }
        catch (Exception ex) when (IsConnectionLoss(ex, ct))
        {
            throw new SteamApiException("Lost the connection to Steam during login. Try again.", null, ex);
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool IsConnectionLoss(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested && ex is AsyncJobFailedException or TaskCanceledException;

    private static async Task<AuthPollResult> PollAsync(CredentialsAuthSession session, Connection conn, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + PollTimeout;
        while (true)
        {
            var result = await conn.CallAsync(() => session.PollAuthSessionStatusAsync()).ConfigureAwait(false);
            if (result is not null) return result;
            if (DateTimeOffset.UtcNow > deadline) throw new SteamApiException("Steam did not finish the login in time. Try again.");
            await Task.Delay(session.PollingInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>A SteamClient plus callback pump that can reconnect on demand.</summary>
    private sealed class Connection : IAsyncDisposable
    {
        private readonly CallbackManager _manager;
        private readonly CancellationTokenSource _cts;
        private readonly Task _pump;
        private readonly CancellationToken _ct;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private TaskCompletionSource _connected = NewSignal();

        public Connection(CancellationToken ct)
        {
            _ct = ct;
            Client = new SteamClient();
            _manager = new CallbackManager(Client);
            _manager.Subscribe<SteamClient.ConnectedCallback>(_ => _connected.TrySetResult());
            _manager.Subscribe<SteamClient.DisconnectedCallback>(_ => _connected.TrySetException(new SteamApiException("Could not connect to Steam.")));
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _pump = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested) await _manager.RunWaitCallbackAsync(_cts.Token).ConfigureAwait(false);
            }, CancellationToken.None);
        }

        public SteamClient Client { get; }

        public async Task EnsureConnectedAsync()
        {
            await _gate.WaitAsync(_ct).ConfigureAwait(false);
            try
            {
                if (Client.IsConnected) return;
                _connected = NewSignal();
                Client.Connect();
                try { await _connected.Task.WaitAsync(ConnectTimeout, _ct).ConfigureAwait(false); }
                catch (TimeoutException ex) { throw new SteamApiException("Timed out connecting to Steam.", null, ex); }
            }
            finally { _gate.Release(); }
        }

        /// <summary>Runs one Steam call; if the connection dropped, reconnects and retries (3 attempts).</summary>
        public async Task<T> CallAsync<T>(Func<Task<T>> op)
        {
            for (var attempt = 1; ; attempt++)
            {
                _ct.ThrowIfCancellationRequested();
                await EnsureConnectedAsync().ConfigureAwait(false);
                try { return await op().ConfigureAwait(false); }
                catch (Exception ex) when (attempt < 3 && IsConnectionLoss(ex, _ct))
                {
                    if (Client.IsConnected) Client.Disconnect();
                    await Task.Delay(TimeSpan.FromSeconds(attempt), _ct).ConfigureAwait(false);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Client.IsConnected) Client.Disconnect();
            await _cts.CancelAsync().ConfigureAwait(false);
            try { await _pump.ConfigureAwait(false); } catch (OperationCanceledException) { }
            _cts.Dispose();
            _gate.Dispose();
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Authenticator(SteamGuardAccount? account, ILoginPrompts prompts, SteamTime time, Connection conn, CancellationToken ct) : IAuthenticator
    {
        private int _generatedAttempts;

        /// <summary>The last code handed to SteamKit, so it can be resent if the connection drops.</summary>
        public (string Value, EAuthSessionGuardType Type)? LastCode { get; private set; }

        public Task<bool> AcceptDeviceConfirmationAsync() => Task.FromResult(false);

        public async Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
        {
            string code;
            if (account?.SharedSecret is { Length: > 0 } secret)
            {
                if (previousCodeWasIncorrect)
                {
                    if (++_generatedAttempts >= 3)
                        throw new SteamGuardException("Steam keeps rejecting the generated codes. Is this maFile still the active authenticator for the account?");
                    await time.SyncAsync(force: true, ct).ConfigureAwait(false);
                    // Wait for the next 30 s window instead of a blind 30 s sleep.
                    await Task.Delay(TimeSpan.FromSeconds(SteamGuardCode.SecondsRemaining(time.Now) + 1), ct).ConfigureAwait(false);
                }
                code = SteamGuardCode.Generate(secret, await time.NowAsync(ct).ConfigureAwait(false));
            }
            else
            {
                code = (await prompts.GetDeviceCodeAsync(previousCodeWasIncorrect).ConfigureAwait(false))?.Trim()
                       ?? throw new OperationCanceledException("Login cancelled.");
            }
            return await HandOverAsync(code, EAuthSessionGuardType.k_EAuthSessionGuardType_DeviceCode).ConfigureAwait(false);
        }

        public async Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
        {
            var code = (await prompts.GetEmailCodeAsync(email, previousCodeWasIncorrect).ConfigureAwait(false))?.Trim()
                       ?? throw new OperationCanceledException("Login cancelled.");
            return await HandOverAsync(code, EAuthSessionGuardType.k_EAuthSessionGuardType_EmailCode).ConfigureAwait(false);
        }

        private async Task<string> HandOverAsync(string code, EAuthSessionGuardType type)
        {
            if (code.Length == 0) throw new OperationCanceledException("Login cancelled.");
            LastCode = (code, type);
            // The user may have taken minutes to find the code; make sure the connection is alive before SteamKit sends it.
            await conn.EnsureConnectedAsync().ConfigureAwait(false);
            return code;
        }
    }
}
