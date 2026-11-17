using Arcline.SteamGuard.Crypto;
using Arcline.SteamGuard.Models;
using Arcline.SteamGuard.Steam;

namespace Arcline.SteamGuard;

/// <summary>
/// One-stop entry point for apps (desktop UI, CLI, or your own programs).
/// <code>
/// using var vault = MaFileVault.Open(MaFileVault.DefaultDirectory());
/// vault.Unlock(passphrase);
/// var client = new SteamGuardClient(vault);
/// var acc = vault.Load(vault.Resolve("myaccount").SteamId);
/// var code = await client.GetCodeAsync(acc);
/// </code>
/// </summary>
public sealed class SteamGuardClient
{
    public SteamGuardClient(IAccountStateStore? store = null, SteamHttp? http = null, SteamTime? time = null)
    {
        Http = http ?? SteamHttp.Shared;
        Time = time ?? (http is null ? SteamTime.Shared : new SteamTime(Http));
        Store = store ?? new InMemoryAccountStateStore();
        Sessions = new SessionService(Http, Store);
        Confirmations = new ConfirmationService(Http, Time, Sessions, Store);
        Login = new SteamLoginService(Time);
    }

    public SteamHttp Http { get; }
    public SteamTime Time { get; }
    public IAccountStateStore Store { get; }
    public SessionService Sessions { get; }
    public ConfirmationService Confirmations { get; }
    public SteamLoginService Login { get; }

    public async Task<string> GetCodeAsync(SteamGuardAccount account, CancellationToken ct = default) =>
        SteamGuardCode.Generate(account.SharedSecret ?? throw new SteamGuardException("shared_secret is missing."),
            await Time.NowAsync(ct).ConfigureAwait(false));

    /// <summary>Code for the current window using the cached time offset (no network; call Time.SyncAsync once at startup).</summary>
    public string GetCode(SteamGuardAccount account) =>
        SteamGuardCode.Generate(account.SharedSecret ?? throw new SteamGuardException("shared_secret is missing."), Time.Now);

    /// <summary>Logs in again and stores the new tokens in the account (e.g. after the refresh token expired).</summary>
    public async Task ReloginAsync(SteamGuardAccount account, string password, ILoginPrompts prompts, CancellationToken ct = default)
    {
        var session = await Login.LoginAsync(account.AccountName ?? throw new SteamGuardException("account_name is missing."),
            password, prompts, account, ct).ConfigureAwait(false);
        if (account.SteamId != 0 && session.SteamId != account.SteamId)
            throw new SteamGuardException("Logged in to a different account than this maFile belongs to.");
        session.SessionId = account.Session?.SessionId;
        account.Session = session;
        await Store.SessionUpdatedAsync(account, ct).ConfigureAwait(false);
    }

    public AuthenticatorLinker CreateLinker(SessionData session) => new(Http, Time, session);
}
