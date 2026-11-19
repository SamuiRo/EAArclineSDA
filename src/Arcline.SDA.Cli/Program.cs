using System.Text.Json;
using Arcline.SDA.Cli;
using Arcline.SteamGuard;
using Arcline.SteamGuard.Crypto;
using Arcline.SteamGuard.Legacy;
using Arcline.SteamGuard.Models;
using Arcline.SteamGuard.Steam;
using Arcline.SteamGuard.Storage;

const string PassEnv = "ARCLINE_SDA_PASSPHRASE";
const string TransferEnv = "ARCLINE_SDA_TRANSFER_PASSPHRASE";

var args0 = new Args(args);
if (args0.Flag("version") || args0.Positionals.FirstOrDefault() == "version")
{
    var v = typeof(MaFileVault).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "?";
    Console.WriteLine($"arcline-sda {v.Split('+')[0]}");
    return 0;
}
if (args0.Positionals.Count == 0 || args0.Flag("help") || args0.Flag("h"))
{
    PrintHelp();
    return args0.Positionals.Count == 0 && !args0.Flag("help") && !args0.Flag("h") ? 2 : 0;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;
var json = args0.Flag("json");
var vaultDir = args0.Opt("vault") ?? MaFileVault.DefaultDirectory();
var cmd = args0.Positionals[0].ToLowerInvariant();
var a = new Args(args.Skip(1)); // command-local view (positional 0 = first command argument)

try
{
    switch (cmd)
    {
        case "init": Init(); break;
        case "list": List(); break;
        case "code": await Code(); break;
        case "confs": case "confirmations": await Confs(); break;
        case "accept": await Respond(true); break;
        case "deny": await Respond(false); break;
        case "ack-trade-protection": await Ack(); break;
        case "refresh": await Refresh(); break;
        case "login": await Login(); break;
        case "link": await Link(); break;
        case "import": Import(); break;
        case "import-sda": ImportSda(); break;
        case "export": Export(); break;
        case "decrypt": Decrypt(); break;
        case "encrypt": Encrypt(); break;
        case "passwd": Passwd(); break;
        case "backup": Backup(); break;
        case "restore": Restore(); break;
        case "import-vault": ImportVault(); break;
        case "remove": Remove(); break;
        case "deactivate": await Deactivate(); break;
        case "where": Console.WriteLine(vaultDir); break;
        case "cleanup": CleanupCmd(); break;
        default: throw new UsageException($"Unknown command '{cmd}'.");
    }
    return 0;
}
catch (UsageException ex) { Console.Error.WriteLine("error: " + ex.Message); Console.Error.WriteLine("Run 'arcline-sda --help'."); return 2; }
catch (WrongPassphraseException ex) { Console.Error.WriteLine("error: " + ex.Message); return 3; }
catch (SessionExpiredException ex) { Console.Error.WriteLine("error: " + ex.Message + $" Run: arcline-sda login <account>"); return 4; }
catch (SteamGuardException ex) { Console.Error.WriteLine("error: " + ex.Message); return 1; }
catch (OperationCanceledException) { Console.Error.WriteLine("cancelled"); return 130; }

// ------------------------------------------------------------------ helpers

MaFileVault OpenVault(bool unlock = true)
{
    if (!MaFileVault.Exists(vaultDir))
        throw new SteamGuardException($"No vault in '{vaultDir}'. Create one with 'arcline-sda init' (or pass --vault <dir>).");
    var v = MaFileVault.Open(vaultDir);
    if (unlock) v.Unlock(ConsoleIO.GetSecret(a, "passphrase-file", PassEnv, "Vault passphrase: "));
    return v;
}

(MaFileVault vault, SteamGuardAccount acc, SteamGuardClient client) OpenAccount()
{
    var v = OpenVault();
    var entry = v.Resolve(a.Pos(0, "account (name or SteamID)"));
    return (v, v.Load(entry.SteamId), new SteamGuardClient(v));
}

void Out(object data, Func<string> text)
{
    if (json) Console.WriteLine(JsonSerializer.Serialize(data, Json.Options));
    else Console.WriteLine(text());
}

// ------------------------------------------------------------------ commands

void Init()
{
    if (MaFileVault.Exists(vaultDir)) throw new SteamGuardException($"Vault already exists in '{vaultDir}'.");
    var pass = ConsoleIO.NewSecret(a, "passphrase-file", PassEnv, "vault passphrase");
    using var v = MaFileVault.Create(vaultDir, pass);
    Console.Error.WriteLine($"Vault created in {v.Directory}. All maFiles stored here are encrypted (AES-256-GCM).");
}

void List()
{
    using var v = OpenVault(unlock: false);
    var rows = v.Entries.Select(e => new { steamid = e.SteamId, account_name = e.AccountName, trade_protection_acknowledged = e.TradeProtectionAcknowledged }).ToList();
    Out(rows, () => rows.Count == 0 ? "(no accounts)" : string.Join(Environment.NewLine, rows.Select(r => $"{r.steamid}  {r.account_name}")));
}

async Task Code()
{
    var (v, acc, client) = OpenAccount();
    using var _ = v;
    do
    {
        var t = await client.Time.NowAsync(ct);
        var code = SteamGuardCode.Generate(acc.SharedSecret!, t);
        var left = SteamGuardCode.SecondsRemaining(t);
        Out(new { account_name = acc.AccountName, code, valid_for_seconds = left }, () => a.Flag("watch") ? $"{code}  ({left,2}s)" : code);
        if (a.Flag("watch")) await Task.Delay(TimeSpan.FromSeconds(left), ct);
    } while (a.Flag("watch"));
}

async Task Confs()
{
    var (v, acc, client) = OpenAccount();
    using var _ = v;
    var list = await client.Confirmations.GetAsync(acc, ct);
    Out(list.Select(ToJson), () => list.Count == 0 ? "Nothing to confirm." :
        string.Join(Environment.NewLine, list.Select(c => $"{c.Id,-20} {c.Type,-14} {c.Headline} | {string.Join(" / ", c.Summary ?? [])}")));
}

async Task Respond(bool accept)
{
    var (v, acc, client) = OpenAccount();
    using var _ = v;
    var list = await client.Confirmations.GetAsync(acc, ct);
    var ids = a.Opts("id").Select(ulong.Parse).ToHashSet();
    var type = a.Opt("type")?.ToLowerInvariant();
    IEnumerable<Confirmation> selected = a.Flag("all") ? list : list.Where(c => ids.Contains(c.Id));
    if (type is not null)
        selected = selected.Where(c => type switch
        {
            "trade" => c.Type == ConfirmationType.Trade,
            "market" => c.Type == ConfirmationType.MarketListing,
            "purchase" => c.Type == ConfirmationType.Purchase,
            _ => c.Type.ToString().Equals(type, StringComparison.OrdinalIgnoreCase),
        });
    var chosen = selected.ToList();
    if (!a.Flag("all") && ids.Count == 0) throw new UsageException("Pass --id <id> (repeatable) or --all.");
    if (chosen.Count == 0) { Out(Array.Empty<object>(), () => "No matching confirmations."); return; }

    var results = await client.Confirmations.RespondAsync(acc, chosen, accept, ct);
    Out(results, () => string.Join(Environment.NewLine, results.Select(r => $"{r.ConfirmationId}: {(r.Success ? "ok" : "FAILED " + r.Message)}")));
    if (results.Any(r => !r.Success)) Environment.ExitCode = 1;
}

async Task Ack()
{
    var (v, acc, client) = OpenAccount();
    using var _ = v;
    await client.Confirmations.AcknowledgeTradeProtectionAsync(acc, ct);
    Out(new { acknowledged = true }, () => "Trade protection notice acknowledged.");
}

async Task Refresh()
{
    var (v, acc, client) = OpenAccount();
    using var _ = v;
    await client.Sessions.RefreshAsync(acc, ct);
    Out(new { refreshed = true, refresh_token_expires = acc.Session?.RefreshTokenExpiresAt },
        () => $"Session refreshed. Refresh token valid until {acc.Session?.RefreshTokenExpiresAt:yyyy-MM-dd}.");
}

async Task Login()
{
    var (v, acc, client) = OpenAccount();
    using var _ = v;
    var password = ConsoleIO.ReadSecret($"Steam password for {acc.AccountName}: ");
    await client.ReloginAsync(acc, password, new ConsoleLoginPrompts(), ct);
    Console.Error.WriteLine("Logged in; new session saved (encrypted).");
}

async Task Link()
{
    using var v = OpenVault();
    var client = new SteamGuardClient(v);
    var username = a.Pos(0, "Steam username");
    var password = ConsoleIO.ReadSecret($"Steam password for {username}: ");
    var session = await client.Login.LoginAsync(username, password, new ConsoleLoginPrompts(), null, ct);
    var linker = client.CreateLinker(session);

    while (true)
    {
        var r = await linker.AddAsync(ct);
        switch (r)
        {
            case AuthenticatorLinker.LinkResult.AwaitingFinalization: goto finalize;
            case AuthenticatorLinker.LinkResult.MustProvidePhoneNumber:
                linker.PhoneNumber = ConsoleIO.ReadLine("Account has no phone. Phone number in international format (+380...): ")?.Trim();
                if (string.IsNullOrEmpty(linker.PhoneNumber)) throw new OperationCanceledException();
                break;
            case AuthenticatorLinker.LinkResult.MustConfirmEmail:
                ConsoleIO.ReadLine($"Click the link Steam e-mailed to {linker.ConfirmationEmailAddress ?? "you"}, then press Enter...");
                break;
            case AuthenticatorLinker.LinkResult.FailureAddingPhone:
                Console.Error.WriteLine("Steam rejected that phone number.");
                linker.PhoneNumber = null;
                break;
            case AuthenticatorLinker.LinkResult.AuthenticatorPresent:
                await TransferExisting(v, linker, username);
                return;
            default:
                throw new SteamGuardException("Steam refused to add the authenticator. " + linker.LastError);
        }
    }

finalize:
    var acc = linker.LinkedAccount!;
    acc.AccountName ??= username;
    v.Save(acc); // secrets are saved (encrypted) BEFORE finalizing; losing them would lock the account
    Console.Error.WriteLine();
    Console.Error.WriteLine($"REVOCATION CODE: {acc.RevocationCode}   <-- write it down now, it is the only way to remove the authenticator without the maFile.");
    while (true)
    {
        var code = ConsoleIO.ReadLine(linker.ConfirmType == 1 ? "SMS code: " : "Code from the e-mail Steam sent: ");
        if (string.IsNullOrWhiteSpace(code)) throw new OperationCanceledException();
        var rev = ConsoleIO.ReadLine("Type the revocation code to prove you saved it: ");
        if (!string.Equals(rev?.Trim(), acc.RevocationCode, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Revocation code does not match.");
            continue;
        }
        var f = await linker.FinalizeAsync(code, ct);
        if (f == AuthenticatorLinker.FinalizeResult.Success) break;
        if (f == AuthenticatorLinker.FinalizeResult.BadCode) { Console.Error.WriteLine("Wrong code, try again."); continue; }
        throw new SteamGuardException($"Finalizing failed ({f}). The encrypted maFile is kept in the vault; revocation code: {acc.RevocationCode}");
    }
    v.Save(acc);
    Console.Error.WriteLine($"Authenticator linked for {acc.AccountName}.");
}

async Task TransferExisting(MaFileVault v, AuthenticatorLinker linker, string username)
{
    Console.Error.WriteLine("This account already has a mobile authenticator (e.g. the Steam app).");
    if (!ConsoleIO.Confirm("Move it to Arcline? Steam texts a code to the account's phone; the OLD authenticator then stops working and Steam may hold trades for a while.", a.Flag("yes")))
        throw new SteamGuardException("Cancelled. If you have its maFile, use 'arcline-sda import <file>' instead.");
    var problem = await linker.TransferStartAsync(ct);
    if (problem is not null) throw new SteamGuardException(problem);
    for (var attempt = 1; ; attempt++)
    {
        var sms = ConsoleIO.ReadLine("SMS code: ");
        if (string.IsNullOrWhiteSpace(sms)) throw new OperationCanceledException();
        var r = await linker.TransferFinishAsync(sms, ct);
        if (r == AuthenticatorLinker.TransferResult.Success) break;
        if (r == AuthenticatorLinker.TransferResult.BadCode && attempt < 3) { Console.Error.WriteLine("That code did not work."); continue; }
        throw new SteamGuardException("Steam did not accept the move; the old authenticator is still active. " + linker.LastError);
    }
    var acc = linker.LinkedAccount!;
    acc.AccountName ??= username;
    v.Save(acc);
    Console.Error.WriteLine($"Authenticator moved to Arcline for {acc.AccountName}.");
    Console.Error.WriteLine($"NEW REVOCATION CODE: {acc.RevocationCode}   <-- write it down.");
}

void Import()
{
    using var v = OpenVault();
    var file = a.Pos(0, "maFile path");
    string? filePass = null;
    if (MaFileEnvelope.LooksLikeEnvelope(File.ReadAllText(file)))
        filePass = ConsoleIO.GetSecret(a, "file-passphrase-file", TransferEnv, "Passphrase of this maFile: ");
    var acc = v.Import(file, filePass, a.Flag("overwrite"), a.Flag("shred"));
    Console.Error.WriteLine($"Imported {acc.DisplayName} ({acc.SteamId}).{(a.Flag("shred") ? " Source file shredded." : " Delete the source file if it was plaintext (or use --shred).")}");
}

void ImportSda()
{
    using var v = OpenVault();
    var folder = a.Pos(0, "SDA folder");
    string? passKey = null;
    if (LegacySdaImporter.IsEncrypted(LegacySdaImporter.ResolveMaFilesDir(folder)))
        passKey = ConsoleIO.ReadSecret("Old SDA encryption passkey: ");
    var res = v.ImportLegacySda(folder, passKey, a.Flag("overwrite"));
    foreach (var acc in res.Accounts) Console.Error.WriteLine($"imported {acc.DisplayName}");
    foreach (var e in res.Errors) Console.Error.WriteLine($"FAILED  {e}");
    if (res.Errors.Count > 0) Environment.ExitCode = 1;
}

void Export()
{
    using var v = OpenVault();
    var entry = v.Resolve(a.Pos(0, "account"));
    var outPath = a.Opt("out") ?? throw new UsageException("--out <file> is required.");
    if (a.Flag("plaintext"))
    {
        if (!ConsoleIO.Confirm("Plaintext export: anyone who gets this file controls the account. Continue?", a.Flag("yes"))) return;
        v.Export(entry.SteamId, outPath, ExportMode.Plaintext);
        Console.Error.WriteLine($"Wrote PLAINTEXT {outPath}. Delete it as soon as it has been imported.");
    }
    else
    {
        var tp = ConsoleIO.NewSecret(a, "transfer-passphrase-file", TransferEnv, "transfer passphrase");
        v.Export(entry.SteamId, outPath, ExportMode.Encrypted, tp);
        Console.Error.WriteLine($"Wrote encrypted {outPath} (arcline-mafile v1, standalone; decrypt with the transfer passphrase).");
    }
}

void Decrypt()
{
    // Works on any standalone arcline-mafile, no vault needed. Meant for one-off imports into other tools.
    var file = a.Pos(0, "encrypted maFile");
    var pass = ConsoleIO.GetSecret(a, "passphrase-file", TransferEnv, "Passphrase of this maFile: ");
    var acc = MaFileVault.ReadExternalFile(file, pass);
    var outPath = a.Opt("out");
    if (outPath is null) Console.WriteLine(acc.ToJson());
    else
    {
        SecureFile.WriteAllText(outPath, acc.ToJson());
        Console.Error.WriteLine($"Wrote PLAINTEXT {outPath}.");
    }
}

void Encrypt()
{
    // Turn a plaintext SDA maFile into a standalone encrypted file (no vault needed).
    var file = a.Pos(0, "plaintext maFile");
    var outPath = a.Opt("out") ?? throw new UsageException("--out <file> is required.");
    var acc = MaFileVault.ReadExternalFile(file, null);
    var pass = ConsoleIO.NewSecret(a, "passphrase-file", TransferEnv, "passphrase for the file");
    MaFileVault.ValidateNewPassphrase(pass);
    using var c = new MaFileCrypto(pass);
    SecureFile.WriteAllText(outPath, c.Encrypt(acc).ToJson());
    if (a.Flag("shred")) SecureFile.Shred(file);
    Console.Error.WriteLine($"Wrote encrypted {outPath}.");
}

void Passwd()
{
    using var v = OpenVault();
    var np = ConsoleIO.NewSecret(new Args([]), "x", "ARCLINE_SDA_NEW_PASSPHRASE", "vault passphrase");
    v.ChangePassphrase(np);
    Console.Error.WriteLine("Passphrase changed; all maFiles re-encrypted with a new salt.");
}

void Backup()
{
    using var v = OpenVault(unlock: false);
    var outPath = a.Opt("out") ?? a.Pos(0, "backup .zip path");
    v.Backup(outPath);
    Console.Error.WriteLine($"Backup written to {outPath} ({v.Entries.Count} account(s), still encrypted; restore it with the same vault passphrase).");
}

void Restore()
{
    var source = a.Pos(0, "backup .zip or vault folder");
    MaFileVault.Restore(source, vaultDir);
    using var v = OpenVault(); // proves the passphrase works on this machine
    Console.Error.WriteLine($"Restored {v.Entries.Count} account(s) into {v.Directory}.");
}

void ImportVault()
{
    using var v = OpenVault();
    var source = a.Pos(0, "backup .zip or vault folder");
    var srcPass = ConsoleIO.GetSecret(a, "source-passphrase-file", "ARCLINE_SDA_SOURCE_PASSPHRASE", "Passphrase of the OTHER vault: ");
    var res = v.ImportVault(source, srcPass, a.Flag("overwrite"));
    foreach (var acc in res.Accounts) Console.Error.WriteLine($"imported {acc.DisplayName}");
    foreach (var e in res.Errors) Console.Error.WriteLine($"FAILED  {e}");
    if (res.Errors.Count > 0) Environment.ExitCode = 1;
}

void CleanupCmd()
{
    var includeVault = a.Flag("include-vault");
    Console.Error.WriteLine("Removes what Arcline stored outside its folder: autostart entry, saved auto-unlock passphrase, logs, old temp files.");
    if (includeVault)
    {
        Console.Error.WriteLine($"--include-vault: ALSO permanently deletes the vault at {vaultDir} (every account in it).");
        if (!a.Flag("yes") && ConsoleIO.ReadLine("Type DELETE to confirm: ")?.Trim() != "DELETE") throw new OperationCanceledException();
    }
    else if (!ConsoleIO.Confirm("Continue? (the vault is kept)", a.Flag("yes"))) return;
    var report = Arcline.SteamGuard.Platform.Cleanup.Run(includeVault, vaultDir);
    foreach (var r in report.Removed) Console.Error.WriteLine($"removed  {r}");
    foreach (var f in report.Failed) Console.Error.WriteLine($"FAILED   {f}");
    if (report.Removed.Count == 0 && report.Failed.Count == 0) Console.Error.WriteLine("Nothing to remove.");
    if (report.Failed.Count > 0) Environment.ExitCode = 1;
}

void Remove()
{
    using var v = OpenVault();
    var entry = v.Resolve(a.Pos(0, "account"));
    if (!ConsoleIO.Confirm($"Remove {entry.DisplayName} from the vault? (this does NOT remove the authenticator from Steam)", a.Flag("yes"))) return;
    v.Remove(entry.SteamId, keepBackup: !a.Flag("no-backup"));
    Console.Error.WriteLine(a.Flag("no-backup") ? "Removed." : "Removed; encrypted copy kept in backups/.");
}

async Task Deactivate()
{
    var (v, acc, client) = OpenAccount();
    using var _ = v;
    var scheme = (a.Opt("scheme") ?? "email") == "none" ? 2 : 1;
    if (!ConsoleIO.Confirm($"Remove the Steam Guard authenticator from {acc.AccountName} ({(scheme == 2 ? "no Steam Guard at all" : "back to e-mail codes")})?", a.Flag("yes"))) return;
    await client.Sessions.EnsureFreshAsync(acc, ct);
    if (!await AuthenticatorLinker.RemoveAsync(client.Http, acc, scheme, ct)) throw new SteamGuardException("Steam refused to remove the authenticator.");
    v.Remove(acc.SteamId, keepBackup: true);
    Console.Error.WriteLine("Authenticator removed. The encrypted maFile was moved to backups/.");
}

static object ToJson(Confirmation c) => new
{
    id = c.Id, nonce = c.Nonce, type = c.Type.ToString(), type_id = c.RawType, creator_id = c.CreatorId,
    headline = c.Headline, summary = c.Summary, created = c.Created,
};

static void PrintHelp() => Console.WriteLine("""
Arcline SDA - cross-platform Steam Desktop Authenticator (encrypted maFiles by default)

Usage: arcline-sda <command> [args] [--vault <dir>] [--json]

Vault
  init                         Create an encrypted vault
  list                         List accounts (no passphrase needed)
  passwd                       Change the vault passphrase (re-encrypts every file)
  where                        Print the vault directory
  cleanup [--include-vault]    Remove app data outside the app folder (autostart, saved passphrase, logs)

Codes and confirmations
  code <acc> [--watch]         Current Steam Guard code
  confs <acc>                  List pending confirmations
  accept <acc> --id N | --all [--type trade|market|purchase]
  deny   <acc> --id N | --all [--type ...]
  ack-trade-protection <acc>   Accept Steam's one-time trade protection notice (SDA issue #1111)

Sessions and authenticator
  refresh <acc>                Refresh the web session (also rotates the refresh token when allowed)
  login <acc>                  Log in again when the session has expired
  link <username>              Add a new authenticator to an account
  deactivate <acc> [--scheme email|none]
  remove <acc> [--no-backup]   Remove from the vault (does not touch Steam)

Moving to another PC
  backup <file.zip>                        Whole vault in one .zip (stays encrypted)
  restore <file.zip|folder>                New PC: recreate the vault (same passphrase)
  import-vault <file.zip|folder> [--overwrite]  Merge another vault into this one (asks its passphrase)

Moving maFiles
  import <file> [--shred] [--overwrite]    Import plaintext SDA JSON or an encrypted arcline-mafile
  import-sda <folder>                      Import an old SDA install (encrypted or not)
  export <acc> --out <file> [--plaintext]  Encrypted with a transfer passphrase (default) or plaintext
  decrypt <file> [--out <file>]            Decrypt a standalone arcline-mafile (no vault needed)
  encrypt <file> --out <file> [--shred]    Encrypt a plaintext maFile into a standalone file

Secrets are never taken as arguments. Sources, in order:
  --passphrase-file <f> / $ARCLINE_SDA_PASSPHRASE                 vault passphrase
  --transfer-passphrase-file <f> / $ARCLINE_SDA_TRANSFER_PASSPHRASE  export/import/decrypt passphrase
  otherwise an interactive prompt.
Vault location: --vault, $ARCLINE_SDA_VAULT, ./vault next to the binary, or the per-user config dir.
Exit codes: 0 ok, 1 error, 2 usage, 3 wrong passphrase, 4 session expired.
""");
