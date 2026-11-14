namespace Deguffer.Core.Tests;

/// <summary>
/// A XAML file stays under 600 lines, or has a recorded budget that may not grow. Past that length a
/// page holds several parts that change for different reasons: repeated card chrome has to be fixed
/// in three places, unrelated edits collide in one file, and a comment goes stale beside a line
/// nobody re-reads. A file over the limit shrinks by moving a part into a control of its own.
///
/// <para>G1 in CLAUDE.md: past about 250 lines, look for the seam. Raising a budget is the
/// maintainer's decision, never a way to make a change pass.</para>
/// </summary>
public sealed class DesignMarkupLengthTests
{
    private const int Limit = 600;

    private static readonly Dictionary<string, int> Budgets = new(StringComparer.Ordinal)
    {
        ["Deguffer.App/Views/CleanPage.xaml"] = 927,
        ["Deguffer.App/Views/DuplicatesPage.xaml"] = 627,
        ["Deguffer.App/Views/ExplorePage.xaml"] = 1438,
        ["Deguffer.App/Views/SettingsPage.xaml"] = 649,
    };

    [Fact]
    public void NoMarkupFileGrowsPastItsBudget()
    {
        var over = ShellMarkup.Files
            .Where(file => file.Lines > Math.Max(Limit, Budgets.GetValueOrDefault(file.Name)))
            .Select(file => $"{file.Name} is {file.Lines} lines, over {Math.Max(Limit, Budgets.GetValueOrDefault(file.Name))}")
            .ToList();

        Assert.True(over.Count == 0, $"{string.Join("; ", over)}. Move a part into a control of its own rather than raising the budget.");
    }

    [Fact]
    public void ABudgetIsDroppedOnceItsFileIsBackUnderTheLimit()
    {
        var stale = Budgets.Keys
            .Where(name => ShellMarkup.Files.FirstOrDefault(file => file.Name == name) is not { } file || file.Lines <= Limit)
            .ToList();

        Assert.True(stale.Count == 0, $"Remove the budgets for {string.Join(", ", stale)}: the files no longer exist or are under {Limit} lines.");
    }
}
