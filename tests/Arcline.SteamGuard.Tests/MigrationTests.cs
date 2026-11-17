using System.IO.Compression;
using System.Text.Json.Nodes;
using Arcline.SteamGuard.Crypto;
using Arcline.SteamGuard.Storage;

namespace Arcline.SteamGuard.Tests;

/// <summary>Moving a vault between PCs: backup .zip, restore on a fresh machine, merge into an existing vault.</summary>
public class MigrationTests
{
    private const string Pass = "old pc passphrase";
    private const int Fast = MaFileEnvelope.MinIterations;

    private static (string dir, MaFileVault vault) VaultWith(params (ulong id, string name)[] accounts)
    {
        var dir = TestData.TempDir();
        var v = MaFileVault.Create(dir, Pass, Fast);
        foreach (var (id, name) in accounts) v.Save(TestData.Account(id, name));
        return (dir, v);
    }

    [Fact]
    public void Backup_ThenRestoreOnNewPc_OpensWithSamePassphrase()
    {
        var (_, oldPc) = VaultWith((1, "a"), (2, "b"));
        var secret = oldPc.Load(2).SharedSecret;
        _ = oldPc.SetTradeProtectionAcknowledgedAsync(2, true, default);
        var zip = Path.Combine(TestData.TempDir(), "backup.zip");
        oldPc.Backup(zip);
        oldPc.Dispose();

        // Backup holds no plaintext secrets.
        using (var z = ZipFile.OpenRead(zip))
            foreach (var e in z.Entries)
                using (var r = new StreamReader(e.Open()))
                    Assert.DoesNotContain(secret!, r.ReadToEnd());

        var newPc = Path.Combine(TestData.TempDir(), "vault");
        MaFileVault.Restore(zip, newPc);
        using var v = MaFileVault.Open(newPc);
        v.Unlock(Pass);
        Assert.Equal(2, v.Entries.Count);
        Assert.Equal(secret, v.Load(2).SharedSecret);
        Assert.True(v.IsTradeProtectionAcknowledged(2));
    }

    [Fact]
    public void Restore_FromCopiedFolder_Works_AndRefusesToOverwrite()
    {
        var (dir, v) = VaultWith((1, "a"));
        v.Dispose();
        var target = Path.Combine(TestData.TempDir(), "vault");
        MaFileVault.Restore(dir, target);
        Assert.True(MaFileVault.Exists(target));
        Assert.Throws<SteamGuardException>(() => MaFileVault.Restore(dir, target));
    }

    [Fact]
    public void ImportVault_MergesWithDifferentPassphrases()
    {
        var (_, other) = VaultWith((1, "a"), (2, "b"));
        var zip = Path.Combine(TestData.TempDir(), "other.zip");
        other.Backup(zip);
        other.Dispose();

        using var mine = MaFileVault.Create(TestData.TempDir(), "my own passphrase", Fast);
        mine.Save(TestData.Account(2, "b-existing"));

        Assert.Throws<WrongPassphraseException>(() => mine.ImportVault(zip, "wrong passphrase"));
        var res = mine.ImportVault(zip, Pass);
        Assert.Single(res.Accounts);                 // account 1 imported
        Assert.Single(res.Errors);                   // account 2 already present, not overwritten
        Assert.Equal("b-existing", mine.Load(2).AccountName);
        Assert.Equal(2, mine.Entries.Count);
        // Imported account is now encrypted with MY passphrase / salt.
        Assert.Equal(mine.Index.Kdf.Salt, MaFileEnvelope.Parse(File.ReadAllText(Path.Combine(mine.Directory, "1.maFile"))).Kdf.Salt);
    }

    [Fact]
    public void MaliciousZip_EntriesOutsideVault_AreIgnored()
    {
        var (dir, v) = VaultWith((1, "a"));
        v.Dispose();
        var zip = Path.Combine(TestData.TempDir(), "evil.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            z.CreateEntryFromFile(Path.Combine(dir, "vault.json"), "vault.json");
            z.CreateEntryFromFile(Path.Combine(dir, "1.maFile"), "1.maFile");
            using var w = new StreamWriter(z.CreateEntry("../../evil.maFile").Open());
            w.Write("pwned");
        }
        var target = Path.Combine(TestData.TempDir(), "vault");
        MaFileVault.Restore(zip, target);
        Assert.Equal(["1.maFile", "vault.json"], Directory.GetFiles(target).Select(f => Path.GetFileName(f)!).Order().ToArray());
    }

    [Fact]
    public void TamperedIndex_WithPathTraversal_IsRejected()
    {
        var (dir, v) = VaultWith((1, "a"));
        v.Dispose();
        var path = Path.Combine(dir, "vault.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        node["accounts"]![0]!["file"] = "../outside.maFile";
        File.WriteAllText(path, node.ToJsonString());
        using var vault = MaFileVault.Open(dir);
        vault.Unlock(Pass);
        Assert.Throws<SteamGuardException>(() => vault.Load(1));
    }
}
