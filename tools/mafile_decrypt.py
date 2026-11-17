#!/usr/bin/env python3
"""Reference decryptor for arcline-mafile v1 (see docs/features/MAFILE_FORMAT.md).

Usage:
    pip install cryptography
    python mafile_decrypt.py account.enc.maFile            # passphrase prompted, JSON to stdout
    ARCLINE_SDA_TRANSFER_PASSPHRASE=... python mafile_decrypt.py account.enc.maFile -o plain.maFile

As a library:
    from mafile_decrypt import decrypt_mafile
    account = decrypt_mafile(open(path).read(), passphrase)   # -> dict (SDA maFile fields)
"""
import argparse
import base64
import getpass
import hashlib
import json
import os
import sys
import unicodedata

from cryptography.hazmat.primitives.ciphers.aead import AESGCM


class WrongPassphrase(Exception):
    pass


def decrypt_mafile(text: str, passphrase: str) -> dict:
    env = json.loads(text)
    if env.get("format") != "arcline-mafile" or env.get("version") != 1:
        raise ValueError("not an arcline-mafile v1 file")
    kdf, cipher = env["kdf"], env["cipher"]
    if kdf["name"] != "pbkdf2-sha256" or cipher["name"] != "aes-256-gcm":
        raise ValueError("unsupported kdf/cipher")

    pw = unicodedata.normalize("NFC", passphrase).encode("utf-8")
    key = hashlib.pbkdf2_hmac("sha256", pw, base64.b64decode(kdf["salt"]), int(kdf["iterations"]), dklen=32)
    aad = "|".join([
        "arcline-mafile", str(env["version"]), kdf["name"], str(kdf["iterations"]), kdf["salt"],
        str(env["steamid"]), env.get("account_name") or "",
    ]).encode("utf-8")
    ct = base64.b64decode(env["ciphertext"]) + base64.b64decode(cipher["tag"])  # AESGCM expects ciphertext||tag
    try:
        plain = AESGCM(key).decrypt(base64.b64decode(cipher["nonce"]), ct, aad)
    except Exception as e:  # InvalidTag
        raise WrongPassphrase("wrong passphrase or corrupted file") from e
    return json.loads(plain.decode("utf-8"))


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("file")
    p.add_argument("-o", "--out", help="write plaintext JSON here instead of stdout")
    a = p.parse_args()
    pw = os.environ.get("ARCLINE_SDA_TRANSFER_PASSPHRASE") or getpass.getpass("Passphrase: ")
    try:
        acc = decrypt_mafile(open(a.file, encoding="utf-8-sig").read(), pw)
    except WrongPassphrase as e:
        print(f"error: {e}", file=sys.stderr)
        return 3
    data = json.dumps(acc, indent=2, ensure_ascii=False)
    if a.out:
        fd = os.open(a.out, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            f.write(data)
        print(f"wrote PLAINTEXT {a.out}", file=sys.stderr)
    else:
        print(data)
    return 0


if __name__ == "__main__":
    sys.exit(main())
