using System.Globalization;
using System.Text;

namespace Deguffer.Core.Tests.CodeRules;

/// <summary>
/// The uses of a rule that predate it, one <c>count key</c> line each, where the key is a
/// repository-relative path or a native entry point. A missing file is an empty baseline.
///
/// <para>A baseline only shrinks. The sweep fails when a use is added and also when one is removed, so
/// the work that removes a use lowers its line in the same change, and the next change cannot spend
/// the room it left.</para>
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

    /// <summary>
    /// What differs between the baseline and the source, written as baseline lines so a removal can
    /// be copied in. A growth is never fixed by copying it in.
    /// </summary>
    public static string Difference(
        string name,
        IReadOnlyDictionary<string, int> expected,
        IReadOnlyDictionary<string, int> found,
        string instead)
    {
        var grew = found.Where(entry => !expected.TryGetValue(entry.Key, out var was) || entry.Value > was).ToList();
        var shrank = expected.Where(entry => !found.TryGetValue(entry.Key, out var now) || now < entry.Value).ToList();
        var text = new StringBuilder();

        if (grew.Count > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Rule \"{name}\" has new uses. {instead}");
            text.AppendLine("Do not add them to the baseline: that is the maintainer's decision, not a way to pass.");

            foreach (var (key, count) in grew)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  {key}: {count}, baseline {expected.GetValueOrDefault(key)}");
            }
        }

        if (shrank.Count > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Rule \"{name}\" has fewer uses than its baseline. Lower Baselines/{name}.txt to:");

            foreach (var (key, _) in shrank)
            {
                text.AppendLine(found.TryGetValue(key, out var now)
                    ? string.Create(CultureInfo.InvariantCulture, $"  {now} {key}")
                    : $"  (remove the line for {key})");
            }
        }

        return text.ToString();
    }
}
