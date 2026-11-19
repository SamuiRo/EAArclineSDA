namespace Arcline.SDA.Cli;

/// <summary>Tiny argument parser: positionals, --flag, --key value (repeatable).</summary>
internal sealed class Args
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> KnownFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "all", "version", "include-vault", "plaintext", "shred", "overwrite", "no-backup", "watch", "yes", "help", "h",
    };

    public List<string> Positionals { get; } = [];

    public Args(IEnumerable<string> argv)
    {
        var list = argv.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (a.StartsWith("--", StringComparison.Ordinal) || (a.StartsWith('-') && a.Length == 2))
            {
                var name = a.TrimStart('-');
                var eq = name.IndexOf('=');
                if (eq > 0)
                {
                    Add(name[..eq], name[(eq + 1)..]);
                }
                else if (KnownFlags.Contains(name) || i + 1 >= list.Count || list[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    _flags.Add(name);
                }
                else
                {
                    Add(name, list[++i]);
                }
            }
            else Positionals.Add(a);
        }
    }

    private void Add(string k, string v)
    {
        if (!_options.TryGetValue(k, out var l)) _options[k] = l = [];
        l.Add(v);
    }

    public bool Flag(string name) => _flags.Contains(name);
    public string? Opt(string name) => _options.TryGetValue(name, out var l) ? l[^1] : null;
    public IReadOnlyList<string> Opts(string name) => _options.TryGetValue(name, out var l) ? l : [];
    public string Pos(int i, string what) => i < Positionals.Count ? Positionals[i] : throw new UsageException($"Missing {what}.");
}

internal sealed class UsageException(string message) : Exception(message);
