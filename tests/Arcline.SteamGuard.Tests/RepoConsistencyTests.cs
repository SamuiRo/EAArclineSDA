using System.Text.RegularExpressions;

namespace Arcline.SteamGuard.Tests;

/// <summary>Keeps versioning honest: the version in Directory.Build.props must have a CHANGELOG entry.</summary>
public partial class RepoConsistencyTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EAArclineSDA.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [GeneratedRegex(@"<Version>([^<]+)</Version>")]
    private static partial Regex VersionRegex();

    [Fact]
    public void CurrentVersion_HasChangelogEntry()
    {
        var root = RepoRoot();
        var version = VersionRegex().Match(File.ReadAllText(Path.Combine(root, "Directory.Build.props"))).Groups[1].Value;
        Assert.Matches(@"^\d+\.\d+\.\d+$", version);
        var changelog = File.ReadAllText(Path.Combine(root, "docs", "project", "CHANGELOG.md"));
        Assert.Contains($"## [{version}]", changelog);
    }

    /// <summary>
    /// The GitHub repo is public. Fails if anything that would be committed (tracked + untracked, not ignored)
    /// looks like account data or a key.
    /// </summary>
    [Fact]
    public void Repository_ContainsNoSecrets()
    {
        var root = RepoRoot();
        var psi = new System.Diagnostics.ProcessStartInfo("git", "ls-files -co --exclude-standard -z")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        string output;
        try
        {
            using var p = System.Diagnostics.Process.Start(psi)!;
            output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) return; // not a git checkout (e.g. source tarball): nothing to check
        }
        catch (System.ComponentModel.Win32Exception) { return; } // git not installed

        var files = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(files);
        var forbiddenName = ForbiddenNameRegex();
        var problems = new List<string>();
        foreach (var f in files)
        {
            if (forbiddenName.IsMatch(f)) { problems.Add($"{f}: forbidden file type"); continue; }
            var full = Path.Combine(root, f);
            if (!File.Exists(full) || new FileInfo(full).Length > 2_000_000) continue;
            var text = File.ReadAllText(full);
            if (text.Contains(PrivateKeyMarker, StringComparison.Ordinal)) problems.Add($"{f}: private key");
            // A real-looking maFile secret literal (tests generate theirs at runtime).
            if (SecretLiteralRegex().IsMatch(text)) problems.Add($"{f}: hard-coded shared/identity secret");
        }
        Assert.True(problems.Count == 0, "Possible secrets in the repository:\n" + string.Join("\n", problems));
    }

    // Split so this file does not match itself.
    private static readonly string PrivateKeyMarker = "PRIVATE " + "KEY-----";

    [GeneratedRegex(@"(\.maFile$|(^|/)vault\.json$|(^|/)manifest\.json$|\.zip$|\.pfx$|\.p12$|\.pem$|(^|/)id_(rsa|ed25519)[^/]*$)", RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenNameRegex();

    [GeneratedRegex("\"(shared_secret|identity_secret)\"\\s*:\\s*\"[A-Za-z0-9+/]{26}[A-Za-z0-9+/=]{2}\"")]
    private static partial Regex SecretLiteralRegex();

    [Fact]
    public void DocsIndex_ListsEveryDoc()
    {
        var docs = Path.Combine(RepoRoot(), "docs");
        var index = File.ReadAllText(Path.Combine(docs, "README.md"));
        foreach (var f in Directory.GetFiles(docs, "*.md", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(docs, f).Replace('\\', '/');
            if (rel == "README.md") continue;
            Assert.True(index.Contains($"({rel})", StringComparison.Ordinal), $"docs/README.md does not link {rel}");
            Assert.StartsWith("> **Role:**", File.ReadLines(f).First(l => l.Trim().Length > 0));
        }
    }
}
