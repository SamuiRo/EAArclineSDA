# Arcline SDA

**English** · [Українська](README.uk.md)

A cross-platform (Windows and Linux) rewrite of [Steam Desktop Authenticator](https://github.com/Jessecar96/SteamDesktopAuthenticator).
- maFiles are encrypted by default.
- Trade confirmations work again after Steam's July 2025 Trade Protection change.
- The core is a library your own programs can use.

Current version: **2.3.0** ([CHANGELOG](docs/project/CHANGELOG.md)).

> Use at your own risk. Whoever holds your maFile and its passphrase controls your Steam account. Keep the passphrase strong and the revocation code written down.

## Download and run

1. Open [Releases](https://github.com/SamuiRo/EAArclineSDA/releases) and download the archive for your OS:
   - Windows: `ArclineSDA-<version>-win-x64.zip`
   - Linux: `ArclineSDA-<version>-linux-x64.tar.gz`
2. Optionally verify it against `SHA256SUMS.txt`.
3. Extract it anywhere and run:
   - **ArclineSDA**: the desktop app;
   - **arcline-sda**: the command-line tool.

You don't need to install .NET; the builds are self-contained.

Notes:
- **Windows:** SmartScreen may warn about an unsigned app. Choose *More info → Run anyway*.
- **Linux:** the tray icon needs StatusNotifier/AppIndicator support. GNOME needs the AppIndicator extension; the app works without it.
- **Where the vault lives:**
  - `%APPDATA%\ArclineSDA\vault` on Windows;
  - `~/.config/ArclineSDA/vault` on Linux;
  - a `vault/` folder next to the executable (portable mode);
  - wherever you point `--vault` or `$ARCLINE_SDA_VAULT`.

**Coming from the old SDA:** *File → Import old SDA folder*, or `arcline-sda import-sda <folder>`. Encrypted SDA folders are supported. The old folder is not modified; delete it once you have checked the codes.

## Features

- **Steam Guard codes**, with time synced to Steam.
- **Confirmations:**
  - checkboxes, *Select all* (Ctrl+A), *Accept / Deny selected*;
  - filter by type (trades / market / other);
  - an *All accounts* view that lists every account's confirmations together;
  - optional background checks, with separate auto-confirm switches for trades, market listings (selling) and purchases. Sensitive types such as API keys or phone changes are never auto-confirmed.
- **Trade Protection fix ([SDA #1111](https://github.com/Jessecar96/SteamDesktopAuthenticator/issues/1111)).** Steam rejects trade confirmations until the account has accepted a one-time notice in the web UI. The old SDA ignored that rejection, so *Confirm* silently did nothing. Arcline acknowledges the notice automatically (`POST /trade/new/acknowledge`, the same request the browser popup sends, as ArchiSteamFarm does), retries once, and reports any confirmation that still fails.
- **Encrypted by default.** There is no unencrypted mode.
  - Every maFile is AES-256-GCM, with the key derived by PBKDF2-SHA256 at 600k iterations.
  - Each file is self-contained, so it can be decrypted without any manifest.
  - Writes are atomic, and files are owner-only (`600`) on Linux.
- **Sessions that survive restarts.** Access tokens are refreshed and saved, refresh tokens rotate, and an expired session shows a "Log in again" hint instead of a stream of pop-ups.
- **Moving between PCs:** *Back up vault* (one encrypted `.zip`) → *Restore from backup* on the new machine, or merge it into an existing vault.
- **Exporting a single account:**
  - encrypted with a separate transfer passphrase, or
  - plaintext SDA-compatible JSON, for a one-off import into another tool (delete it afterwards).
- **Adding a new authenticator** to an account, and removing one.

## Security, honestly

The encryption itself isn't the weak point. AES-256-GCM is not broken; an attacker can only guess your passphrase offline, and PBKDF2 makes every guess expensive. So **the passphrase is what protects you.** Use 4–5 random words or 14+ characters.

The salt and KDF parameters are stored in clear text by design, as in every PBKDF2 scheme. A salt isn't a secret. They are bound to the ciphertext through GCM's authenticated data, so tampering with them is detected.

What encryption does **not** protect against:
- malware on an unlocked machine (keyloggers, memory readers);
- a plaintext export you forgot to delete;
- a weak passphrase.

## Using maFiles in other programs

| How | Use |
|---|---|
| CLI | `arcline-sda decrypt file.maFile` prints SDA-compatible JSON. No vault needed. |
| .NET | Reference `Arcline.SteamGuard`: `MaFileVault.ReadExternalFile(path, passphrase)` |
| Python | `tools/mafile_decrypt.py` (`pip install cryptography`) |
| Any language | Follow the spec in [docs/features/MAFILE_FORMAT.md](docs/features/MAFILE_FORMAT.md): PBKDF2-SHA256 + AES-256-GCM, standard primitives only. |

## CLI

```bash
arcline-sda init
```

```bash
arcline-sda confs myacc --json
```

```bash
arcline-sda accept myacc --all --type trade
```

```bash
arcline-sda export myacc --out myacc.enc.maFile
```

```bash
arcline-sda backup vault.zip
```

```bash
arcline-sda --help
```

Secrets are never accepted as arguments. The CLI reads them from a `--passphrase-file`, from `$ARCLINE_SDA_PASSPHRASE` / `$ARCLINE_SDA_TRANSFER_PASSPHRASE`, or from a prompt.

Exit codes:

| Code | Meaning |
|---|---|
| 0 | OK |
| 1 | Error |
| 2 | Usage |
| 3 | Wrong passphrase |
| 4 | Session expired |

## Building from source

You need the .NET 10 SDK.

```bash
dotnet test EAArclineSDA.slnx
```

```bash
dotnet run --project src/Arcline.SDA.Desktop
```

Pushing a tag `vX.Y.Z` that matches `<Version>` in `Directory.Build.props` builds the release archives on GitHub Actions. Project docs (architecture, decisions, roadmap) are in [docs/](docs/README.md).

| Project | What |
|---|---|
| `src/Arcline.SteamGuard` | Core library: vault and crypto, codes, confirmations, sessions, linking, legacy SDA import |
| `src/Arcline.SDA.Desktop` | Avalonia desktop app |
| `src/Arcline.SDA.Cli` | `arcline-sda` command-line tool |
| `tools/mafile_decrypt.py` | Reference decryptor |

## Status

The protocol code is checked against the original SteamAuth implementation: codes and confirmation signatures match byte for byte. It is unit-tested against a fake Steam, including trade protection, sessions, vault, migration and the UI.

**Not yet verified against a live Steam account:** login, linking, confirmations and authenticator removal. Please report issues.

## License

MIT, see [LICENSE](LICENSE). Based on Steam Desktop Authenticator and SteamAuth (both MIT); see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Not affiliated with Valve.
