using System.Globalization;

namespace Deguffer.Core.Tests.CodeRules;

/// <summary>
/// A code rule's uses that predate it, read from <c>Baselines/&lt;rule&gt;.txt</c>: one <c>count key</c>
/// line each, where the key is a repository-relative path or a native entry point. A missing file is
/// an empty baseline. <see cref="RuleBaseline"/> holds the source to it, so it only shrinks.
///
/// <para>A file rather than a dictionary in code, because a rule such as the plan identity has over a
/// hundred entries, and the rule book would otherwise break the file-length rule it states.</para>
/// </summary>
internal static class Baseline
{
    public static readonly string Folder =
        Path.Combine(MarkdownGuide.RepositoryRoot, "Deguffer.Core.Tests", "CodeRules", "Baselines");

    public static SortedDictionary<string, int> Read(string name)
    {
        var entries = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var file = Path.Combine(Folder, name + ".txt");

        if (!File.Exists(file))
        {
            return entries;
        }

        foreach (var line in File.ReadLines(file))
        {
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var space = line.IndexOf(' ', StringComparison.Ordinal);
            entries.Add(line[(space + 1)..], int.Parse(line.AsSpan(0, space), CultureInfo.InvariantCulture));
        }

        return entries;
    }

    public static IEnumerable<string> Names() =>
        Directory.EnumerateFiles(Folder, "*.txt").Select(Path.GetFileNameWithoutExtension).OfType<string>();
}
