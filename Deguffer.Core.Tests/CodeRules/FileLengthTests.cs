using System.Globalization;

namespace Deguffer.Core.Tests.CodeRules;

/// <summary>
/// No product file grows past 500 lines, and no file that is already past it grows at all.
///
/// <para>A file that long holds more than one responsibility, and becomes the file every change
/// collides in (G1). The files already past it are listed in <c>Baselines/file-length.txt</c> at their
/// length, each a monolith with an issue open to split it. A listed file may shrink, and its line is
/// lowered when it does, but it may not grow back: something new in it is a type of its own, or
/// moves a part out first.
/// <c>ExploreMap.cs</c> is left to <see cref="ExploreMapBudgetTests"/>, which holds its budget.</para>
/// </summary>
public sealed class FileLengthTests
{
    internal const string BaselineName = "file-length";

    private const int Ceiling = 500;

    private const string ExploreMap = "Deguffer.App/Controls/ExploreMap.cs";

    [Fact]
    public void NoProductFileGrowsPastItsCeiling()
    {
        var listed = Baseline.Read(BaselineName);
        var problems = new List<string>();

        foreach (var (path, text) in CodeRuleSweepTests.ProductFiles.Value)
        {
            if (path == ExploreMap)
            {
                continue;
            }

            var lines = Lines(text);
            var allowed = listed.GetValueOrDefault(path, Ceiling);

            if (lines > allowed)
            {
                problems.Add($"{path} is {lines} lines, over its {allowed}. Move a part into a type of its own; "
                    + "never raise the ceiling, which is the maintainer's decision.");
            }
            else if (listed.ContainsKey(path) && lines < allowed)
            {
                problems.Add(lines <= Ceiling
                    ? $"{path} is {lines} lines, within {Ceiling}: remove its line from Baselines/{BaselineName}.txt."
                    : $"{path} shrank to {lines} lines: lower its line in Baselines/{BaselineName}.txt to "
                        + string.Create(CultureInfo.InvariantCulture, $"\"{lines} {path}\"."));
            }
        }

        var present = CodeRuleSweepTests.ProductFiles.Value.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        problems.AddRange(listed.Keys.Where(path => !present.Contains(path))
            .Select(path => $"{path} no longer exists: remove its line from Baselines/{BaselineName}.txt."));

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void TheBaselineListsOnlyFilesPastTheCeiling()
    {
        Assert.All(Baseline.Read(BaselineName), entry => Assert.True(entry.Value > Ceiling, entry.Key));
    }

    /// <summary>Lines as an editor numbers them: a final line break does not start another.</summary>
    internal static int Lines(string text) =>
        text.Length == 0 ? 0 : text.Count(c => c == '\n') + (text[^1] == '\n' ? 0 : 1);
}
