using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Arcline.SteamGuard.Crypto;
using Arcline.SteamGuard.Legacy;
using Arcline.SteamGuard.Models;
using Arcline.SteamGuard.Storage;

namespace Arcline.SteamGuard.Tests;

public class VaultTests
{
    private const string Pass = "correct horse battery";
    private const int FastIterations = MaFileEnvelope.MinIterations; // keep tests quick

    [Fact]
    public void FilesOnDisk_AreNeverPlaintext()
    {
        var dir = TestData.TempDir();
        using var vault = MaFileVault.Create(dir, Pass, FastIterations);
        var acc = TestData.Account();
        vault.Save(acc);

        var raw = File.ReadAllText(Path.Combine(dir, $"{acc.SteamId}.maFile"));
        Assert.DoesNotContain(acc.SharedSecret!, raw);
        Assert.DoesNotContain(acc.IdentitySecret!, raw);
        Assert.DoesNotContain(acc.RevocationCode!, raw);
        Assert.True(MaFileEnvelope.LooksLikeEnvelope(raw));
        Assert.DoesNotContain(acc.SharedSecret!, File.ReadAllText(Path.Combine(dir, MaFileVault.IndexFileName)));
    }

    [Fact]
    public void Reopen_UnlockAndLoad_RoundTrips()
    {
        var dir = TestData.TempDir();
        var acc = TestData.Account();
        using (var v = MaFileVault.Create(dir, Pass, FastIterations)) v.Save(acc);

        using var vault = MaFileVault.Open(dir);
        Assert.Throws<WrongPassphraseException>(() => vault.Unlock("wrong passphrase"));
        vault.Unlock(Pass);
        var loaded = vault.Load(acc.SteamId);
        Assert.Equal(acc.ToJson(), loaded.ToJson());
        Assert.Equal(acc.SteamId, vault.Resolve("TESTACC").SteamId);
    }

    [Fact]
    public void EmptyVault_StillVerifiesPassphrase()
    {
        var dir = TestData.TempDir();
        MaFileVault.Create(dir, Pass, FastIterations).Dispose();
        using var vault = MaFileVault.Open(dir);
        Assert.Throws<WrongPassphraseException>(() => vault.Unlock("not the passphrase"));
        vault.Unlock(Pass);
        Assert.True(vault.IsUnlocked);
    }

    [Fact]
    public void ShortPassphrase_IsRejected()
    {
        Assert.Throws<SteamGuardException>(() => MaFileVault.Create(TestData.TempDir(), "short"));
    }

    [Fact]
    public void TamperedHeader_IsDetected()
    {
        var dir = TestData.TempDir();
        var acc = TestData.Account();
        using var vault = MaFileVault.Create(dir, Pass, FastIterations);
        vault.Save(acc);
        var path = Path.Combine(dir, $"{acc.SteamId}.maFile");
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        node["account_name"] = "someone-else";
        File.WriteAllText(path, node.ToJsonString());
        Assert.Throws<WrongPassphraseException>(() => vault.Load(acc.SteamId));
    }

    [Fact]
    public void EncryptedExport_IsStandaloneAndUsesTransferPassphrase()
    {
        var dir = TestData.TempDir();
        var acc = TestData.Account();
        using var vault = MaFileVault.Create(dir, Pass, FastIterations);
        vault.Save(acc);
        var outFile = Path.Combine(TestData.TempDir(), "export.maFile");
        vault.Export(acc.SteamId, outFile, ExportMode.Encrypted, "transfer-secret-1");

        Assert.Throws<WrongPassphraseException>(() => MaFileVault.ReadExternalFile(outFile, Pass));
        var back = MaFileVault.ReadExternalFile(outFile, "transfer-secret-1");
        Assert.Equal(acc.SharedSecret, back.SharedSecret);
        // Export salt differs from the vault salt.
        Assert.NotEqual(vault.Index.Kdf.Salt, MaFileEnvelope.Parse(File.ReadAllText(outFile)).Kdf.Salt);
    }

    [Fact]
    public void PlaintextExport_IsSdaCompatibleJson()
    {
        var dir = TestData.TempDir();
        var acc = TestData.Account();
        using var vault = MaFileVault.Create(dir, Pass, FastIterations);
        vault.Save(acc);
        var outFile = Path.Combine(TestData.TempDir(), "plain.maFile");
        vault.Export(acc.SteamId, outFile, ExportMode.Plaintext);

        using var doc = JsonDocument.Parse(File.ReadAllText(outFile));
        Assert.Equal(acc.SharedSecret, doc.RootElement.GetProperty("shared_secret").GetString());
        Assert.Equal(acc.SteamId, doc.RootElement.GetProperty("Session").GetProperty("SteamID").GetUInt64());
    }

    [Fact]
    public void ImportPlaintext_EncryptsAndShredsSource()
    {
        var dir = TestData.TempDir();
        var acc = TestData.Account();
        var src = Path.Combine(TestData.TempDir(), "in.maFile");
        File.WriteAllText(src, acc.ToJson());
        using var vault = MaFileVault.Create(dir, Pass, FastIterations);
        vault.Import(src, shredSource: true);
        Assert.False(File.Exists(src));
        Assert.Equal(acc.IdentitySecret, vault.Load(acc.SteamId).IdentitySecret);
    }

    [Fact]
    public void ChangePassphrase_ReencryptsEverything()
    {
        var dir = TestData.TempDir();
        var a = TestData.Account(1001, "a");
        var b = TestData.Account(1002, "b");
        using (var v = MaFileVault.Create(dir, Pass, FastIterations))
        {
            v.Save(a);
            v.Save(b);
            v.ChangePassphrase("brand new passphrase");
        }
        using var vault = MaFileVault.Open(dir);
        Assert.Throws<WrongPassphraseException>(() => vault.Unlock(Pass));
        vault.Unlock("brand new passphrase");
        Assert.Equal(b.SharedSecret, vault.Load(1002).SharedSecret);
    }

    [Fact]
    public void UnknownFields_ArePreserved()
    {
        var json = TestData.Account().ToJson();
        var node = JsonNode.Parse(json)!;
        node["some_future_field"] = 42;
        node["Session"]!["OAuthToken"] = "legacy";
        var acc = SteamGuardAccount.Parse(node.ToJsonString());
        var round = JsonNode.Parse(acc.ToJson())!;
        Assert.Equal(42, round["some_future_field"]!.GetValue<int>());
        Assert.Equal("legacy", round["Session"]!["OAuthToken"]!.GetValue<string>());
    }

    [Fact]
    public void SteamNumbersAsStrings_AreAccepted()
    {
        var node = JsonNode.Parse(TestData.Account().ToJson())!;
        node["server_time"] = "1700000123";
        node["status"] = "1";
        Assert.Equal(1700000123, SteamGuardAccount.Parse(node.ToJsonString()).ServerTime);
    }

    // ---------- legacy SDA ----------

    /// <summary>Verbatim port of the original SDA FileEncryptor.EncryptData (Rfc2898DeriveBytes SHA1 50k + RijndaelManaged CBC).</summary>
    private static string OriginalSdaEncrypt(string password, string salt, string iv, string plaintext)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(password, Convert.FromBase64String(salt), 50000);
        var key = pbkdf2.GetBytes(32);
        using var aes256 = new RijndaelManaged { Key = key, IV = Convert.FromBase64String(iv), Padding = PaddingMode.PKCS7, Mode = CipherMode.CBC };
        using var ms = new MemoryStream();
        using (var cs = new CryptoStream(ms, aes256.CreateEncryptor(aes256.Key, aes256.IV), CryptoStreamMode.Write))
        using (var sw = new StreamWriter(cs)) sw.Write(plaintext);
        return Convert.ToBase64String(ms.ToArray());
    }

    [Fact]
    public void LegacyEncryptedSdaFolder_IsImported()
    {
        var sda = Path.Combine(TestData.TempDir(), "maFiles");
        Directory.CreateDirectory(sda);
        var acc = TestData.Account();
        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(8));
        var iv = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        File.WriteAllText(Path.Combine(sda, $"{acc.SteamId}.maFile"), OriginalSdaEncrypt("oldkey", salt, iv, acc.ToJson(false)));
        File.WriteAllText(Path.Combine(sda, "manifest.json"),
            $$"""{"encrypted":true,"first_run":false,"entries":[{"encryption_iv":"{{iv}}","encryption_salt":"{{salt}}","filename":"{{acc.SteamId}}.maFile","steamid":{{acc.SteamId}}}],"periodic_checking":false}""");

        Assert.Throws<SteamGuardException>(() => LegacySdaImporter.Load(Path.GetDirectoryName(sda)!, null));
        var bad = LegacySdaImporter.Load(sda, "wrong");
        Assert.Empty(bad.Accounts);
        Assert.Single(bad.Errors);

        using var vault = MaFileVault.Create(TestData.TempDir(), Pass, FastIterations);
        var res = vault.ImportLegacySda(Path.GetDirectoryName(sda)!, "oldkey");
        Assert.Empty(res.Errors);
        Assert.Equal(acc.SharedSecret, vault.Load(acc.SteamId).SharedSecret);
    }

    [Fact]
    public void LegacyCrypto_RoundTripsWithOriginal()
    {
        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(8));
        var iv = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        const string text = "{\"shared_secret\":\"x\"} ünïcødé";
        Assert.Equal(OriginalSdaEncrypt("k", salt, iv, text), LegacySdaImporter.Encrypt("k", salt, iv, text));
        Assert.Equal(text, LegacySdaImporter.Decrypt("k", salt, iv, OriginalSdaEncrypt("k", salt, iv, text)));
    }

    [Fact]
    public void Envelope_DecryptsWithSpecAlgorithm()
    {
        // Independent decryption following docs/features/MAFILE_FORMAT.md, to keep the spec honest.
        var acc = TestData.Account();
        using var c = new MaFileCrypto(Pass);
        var env = c.Encrypt(acc, iterations: FastIterations);
        var json = JsonNode.Parse(env.ToJson())!;

        var salt = Convert.FromBase64String(json["kdf"]!["salt"]!.GetValue<string>());
        var iterations = json["kdf"]!["iterations"]!.GetValue<int>();
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(Pass), salt, iterations, HashAlgorithmName.SHA256, 32);
        var aad = Encoding.UTF8.GetBytes(string.Join("|", "arcline-mafile", json["version"], json["kdf"]!["name"], iterations,
            json["kdf"]!["salt"], json["steamid"], json["account_name"]?.GetValue<string>() ?? ""));
        var ct = Convert.FromBase64String(json["ciphertext"]!.GetValue<string>());
        var plain = new byte[ct.Length];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(Convert.FromBase64String(json["cipher"]!["nonce"]!.GetValue<string>()), ct,
            Convert.FromBase64String(json["cipher"]!["tag"]!.GetValue<string>()), plain, aad);
        Assert.Equal(acc.SharedSecret, SteamGuardAccount.Parse(Encoding.UTF8.GetString(plain)).SharedSecret);
    }
}
