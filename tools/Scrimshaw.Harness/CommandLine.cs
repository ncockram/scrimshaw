using System.Globalization;

namespace Scrimshaw.Harness;

/// <summary>Minimal parser: positional arguments plus <c>--name value</c> options and <c>--flag</c> switches.</summary>
internal sealed class CommandLine
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);

    public CommandLine(string[] args)
    {
        var positional = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
                _options[args[i][2..]] = hasValue ? args[++i] : null;
            }
            else
            {
                positional.Add(args[i]);
            }
        }
        Positional = positional;
    }

    public IReadOnlyList<string> Positional { get; }

    public string Arg(int index, string name) =>
        index < Positional.Count ? Positional[index] : throw new ArgumentException($"missing <{name}>");

    public string? Get(string name) => _options.TryGetValue(name, out string? v) ? v : null;

    public bool Has(string name) => _options.ContainsKey(name);

    public double Double(string name, double fallback) =>
        Get(name) is { } v ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;

    public int Int(string name, int fallback) =>
        Get(name) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;
}
