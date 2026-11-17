> **Role:** Per-version record of what changed and why · **Audience:** Anyone deciding whether to upgrade, or tracing when a behavior changed

Versions follow what a user or integrator can observe:
- **patch** — invisible changes: docs, tests, refactors.
- **minor** — new capability; existing behavior is unchanged.
- **major** — anything that can break an existing setup or integration: the maFile/vault format, CLI arguments and exit codes, the public library API.

The single source of the version is `<Version>` in `Directory.Build.props`. A release is published by pushing the matching `vX.Y.Z` tag. The section for that version below becomes the release notes.

Numbering continues the original SDA (last release 1.0.15). 2.0.0 is a major because the storage format is incompatible with SDA's `manifest.json`; existing SDA data is migrated by importing it.

## [Unreleased]

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
