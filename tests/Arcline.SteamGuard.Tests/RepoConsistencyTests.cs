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
