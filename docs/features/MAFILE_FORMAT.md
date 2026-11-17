> **Role:** The encrypted maFile contract as other programs see it · **Audience:** Developers reading or writing arcline-mafile files from any language

# arcline-mafile v1: encrypted maFile format

This is a self-contained encrypted maFile. Everything needed to decrypt it, except the passphrase, is in the file itself. Unlike classic SDA encryption, no `manifest.json` is needed.

Only standard primitives are used: PBKDF2-HMAC-SHA256 and AES-256-GCM. Every mainstream language has them, either in the standard library or in one well-known package.

## File

UTF-8 JSON:

```json
{
  "format": "arcline-mafile",
  "version": 1,
  "account_name": "mylogin",
  "steamid": 76561198000000000,
  "kdf":    { "name": "pbkdf2-sha256", "iterations": 600000, "salt": "<base64, 16 bytes>" },
  "cipher": { "name": "aes-256-gcm", "nonce": "<base64, 12 bytes>", "tag": "<base64, 16 bytes>" },
  "ciphertext": "<base64>"
}
```

`account_name` and `steamid` are stored in clear text, so a program can list accounts without the passphrase. They are covered by the GCM authentication tag, so changing them makes decryption fail.

## Decrypting

1. `pw = UTF-8(NFC-normalize(passphrase))`
2. `key = PBKDF2-HMAC-SHA256(pw, base64decode(kdf.salt), kdf.iterations, 32 bytes)`
3. `aad = UTF-8("arcline-mafile|" + version + "|" + kdf.name + "|" + kdf.iterations + "|" + kdf.salt + "|" + steamid + "|" + (account_name or ""))`
   - `kdf.salt` goes in exactly as the base64 string that appears in the file.
   - Numbers are written in plain decimal.
4. `plaintext = AES-256-GCM-Decrypt(key, nonce, ciphertext, tag, aad)`. A tag mismatch means either a wrong passphrase or a modified file.
5. `plaintext` is UTF-8 JSON in the classic SDA/SteamAuth maFile layout: `shared_secret`, `identity_secret`, `revocation_code`, `device_id`, `account_name`, and `Session { SteamID, AccessToken, RefreshToken, SessionID }`. Other tools may add their own fields; those are preserved.

Readers must reject:

- a `format` or `version` they don't know;
- any `kdf.name` or `cipher.name` other than the ones above;
- `iterations < 100000`.

## Reference implementations

| Language | Where |
|---|---|
| C# / .NET | `Arcline.SteamGuard` → `MaFileVault.ReadExternalFile(path, passphrase)` or `new MaFileCrypto(pass).Decrypt(MaFileEnvelope.Parse(json))` |
| Python | `tools/mafile_decrypt.py` (`pip install cryptography`) |
| Any language, via CLI | `arcline-sda decrypt file.maFile` prints the plaintext JSON to stdout (passphrase from `$ARCLINE_SDA_TRANSFER_PASSPHRASE` or a prompt) |

Node.js sketch:

```js
const crypto = require("crypto");
function decryptMaFile(env, pass) {
  const key = crypto.pbkdf2Sync(Buffer.from(pass.normalize("NFC"), "utf8"),
    Buffer.from(env.kdf.salt, "base64"), env.kdf.iterations, 32, "sha256");
  const aad = Buffer.from(["arcline-mafile", env.version, env.kdf.name, env.kdf.iterations,
    env.kdf.salt, env.steamid, env.account_name ?? ""].join("|"), "utf8");
  const d = crypto.createDecipheriv("aes-256-gcm", key, Buffer.from(env.cipher.nonce, "base64"));
  d.setAAD(aad);
  d.setAuthTag(Buffer.from(env.cipher.tag, "base64"));
  return JSON.parse(Buffer.concat([d.update(Buffer.from(env.ciphertext, "base64")), d.final()]).toString("utf8"));
}
```

> JavaScript parses `steamid` as a double and loses precision for 64-bit SteamIDs. When building the AAD in JS, parse the JSON with a BigInt-aware parser, or take the digits of `steamid` straight from the raw text.

## The vault

The desktop app and the CLI store accounts in a vault directory:

- `vault.json` holds the non-secret index and settings, plus a `verifier` envelope used to check the passphrase.
- Each account is a separate `<steamid>.maFile`, written as an envelope.

All files in one vault share a single salt, so the key is derived only once per unlock. Every exported file gets a fresh salt and can carry its own transfer passphrase.
