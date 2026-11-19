> **Role:** Per-version record of what changed and why · **Audience:** Anyone deciding whether to upgrade, or tracing when a behavior changed

Versions follow what a user or integrator can observe:
- **patch** — invisible changes: docs, tests, refactors.
- **minor** — new capability; existing behavior is unchanged.
- **major** — anything that can break an existing setup or integration: the maFile/vault format, CLI arguments and exit codes, the public library API.

The single source of the version is `<Version>` in `Directory.Build.props`. A release is published by pushing the matching `vX.Y.Z` tag. The section for that version below becomes the release notes.

Numbering continues the original SDA (last release 1.0.15). 2.0.0 is a major because the storage format is incompatible with SDA's `manifest.json`; existing SDA data is migrated by importing it.

## [Unreleased]

## [2.5.0] - 2026-11-19

### Added
- **Auto-confirm per account.** *Account > Auto-confirm...* (or the button under the code) chooses what that account confirms by itself: trades, market listings, purchases, in any combination. Everything else stays manual. Accounts with auto-confirm are checked in the background even when they are not selected. The account list marks them "auto".
- **Tray and startup:**
  - *Start with Windows* / *Start when I log in* starts the app minimized in the tray (HKCU `Run` key on Windows, `~/.config/autostart` on Linux);
  - minimizing or closing keeps the app in the tray;
  - starting it again brings the running instance to the front.
- **Unlock automatically on this PC** (optional, off by default) keeps the vault passphrase in the OS's protected store (Windows DPAPI / Linux Secret Service via `secret-tool`), so auto-confirm works right after startup.
- **Remove app data from this PC** (*File* menu, or `arcline-sda cleanup [--include-vault]`) removes the autostart entry, the saved passphrase, logs and leftovers of older versions, optionally including the vault.

### Changed
- Auto-confirm switches moved from Settings to each account. Switches set in 2.3.0–2.4.0 are copied to every account automatically on first start, so nothing stops working.
- Release builds no longer unpack native libraries into a temp folder (`%TEMP%\.net\ArclineSDA`, `~/.net/...`), where they stayed after the app was deleted. They now ship next to the executables, and the executables are about half the size.

## [2.4.0] - 2026-11-19

### Added
- **Move an existing authenticator to Arcline.** If an account already has a mobile authenticator (for example the Steam app), *Add authenticator* (and `arcline-sda link`) now takes it over with an SMS code, the same way the official app's "use this device" does. Steam issues new secrets, the old authenticator stops working, and the new revocation code is shown. This needs a phone number on the account.
- An error that nothing else caught is now logged to `%APPDATA%\ArclineSDA\logs` (`~/.config/ArclineSDA/logs` on Linux) and shown in a dialog instead of closing the app.

### Fixed
- **The app closed after entering the e-mail code while adding an account.** Steam drops idle connections while you look for the code, and sending the code then failed with an unhandled `AsyncJobFailedException`. The login now reconnects before sending the code, resends it on a fresh connection if needed, and reports a clear error if it still fails.
- *Add authenticator* on an account that already has one used to ask for that authenticator's code and then just say "already has an authenticator". Now it explains up front what will happen and lets you cancel or move the authenticator.

## [2.3.0] - 2026-11-18

### Added
- Separate auto-confirm switches for **trades**, **market listings** (selling) and **purchases** (Steam type 12). Each one is opt-in on its own.
- Other confirmation types are never auto-confirmed: API keys, phone or recovery changes, family invites, and any type Steam adds later.
- The notification after auto-confirming says how many items of each type were confirmed. Auto-confirmations that Steam rejects stay in the list for you to handle, and you get a warning.

### Changed
- Enabling any auto-confirm switch also turns on periodic checking. Without it, auto-confirm would silently do nothing.
- The settings dialog groups the switches under "Auto-confirm without asking", and the warning lists exactly which types are being enabled.

## [2.2.0] - 2026-11-17

### Added
- Ready-to-run releases: self-contained archives for Windows (`.zip`) and Linux (`.tar.gz`) with the desktop app and the CLI, plus `SHA256SUMS.txt`. No .NET installation needed. They are built by GitHub Actions when a version tag is pushed.
- CI on Windows and Linux for every push and pull request.
- English README (default) next to the Ukrainian one.
- MIT license and third-party notices for Steam Desktop Authenticator, SteamAuth and bundled libraries.

### Changed
- In release builds `SteamKit2.dll` (LGPL-2.1) ships as a separate, replaceable file next to the executables.

### Security
- Repository guard: the test suite fails if a maFile, a `vault.json`, an SDA `manifest.json`, an archive, a private key or a hard-coded maFile secret would be committed.

## [2.1.0] - 2026-11-17

### Added
- Confirmations in the desktop app:
  - a checkbox on every card, *Select all* (Ctrl+A), and *Accept / Deny selected (N)*;
  - a type filter (All / Trades / Market / Other);
  - an *All accounts* view that lists the confirmations of every account, with the account name on each card;
  - selected confirmations are sent as one Steam request per account, and any error is shown on its card.

### Changed
- *Accept all* / *Deny all* are replaced by selection plus *Accept / Deny selected*. *Deny selected* asks for confirmation when more than one item is selected.
- Items hidden by the filter are deselected automatically, so they can never be acted on by accident.

## [2.0.0] - 2026-11-17

First release of Arcline SDA, a rewrite of Steam Desktop Authenticator.

### Added
- Cross-platform core library `Arcline.SteamGuard` (.NET 10), the `arcline-sda` CLI with `--json` output, and an Avalonia 11 desktop app for Windows and Linux.
- Encrypted maFile format `arcline-mafile` v1: AES-256-GCM with PBKDF2-SHA256 (600k iterations), one self-contained file per account. Specification: [features/MAFILE_FORMAT.md](../features/MAFILE_FORMAT.md). Reference decryptor: `tools/mafile_decrypt.py`.
- A vault with mandatory encryption.
- Account export, either encrypted with a separate transfer passphrase or as SDA-compatible plaintext. `decrypt` and `encrypt` work without a vault.
- Moving between PCs: `backup` to one `.zip`, `restore` on a new PC, `import-vault` to merge two vaults.
- Import of an old SDA folder (encrypted or not) and of plaintext maFiles, with optional shredding of the source.
- Refresh token rotation, persisted refreshed tokens, HTTP retries with backoff, and periodic Steam time sync.

### Fixed
- **SDA #1111:** trade confirmations stopped working after Steam Trade Protection launched (July 2025).
  - The one-time notice is now acknowledged automatically (`POST /trade/new/acknowledge`).
  - Steam's refusal is now reported instead of silently ignored.
- Refreshed session tokens are no longer lost on restart.
- Background checking no longer stops for good after its first error, and no longer spams dialogs.

### Security
- There is no unencrypted mode, and passphrases are never accepted as command-line arguments.
- Writes are atomic, with owner-only permissions (`600` for files, `700` for directories) on Linux.
- Path-traversal protection for `vault.json` entries and backup archives.
