namespace Deguffer.Core.Tests.CodeRules;

/// <summary>
/// The product source does not grow a pattern the code rules forbid, and does not grow past its
/// baseline of uses that predate a rule.
///
/// <para><b>Read from the source, because these defects compile.</b> A second declaration of a native
/// call, a deletion's age read from the wall clock, or a sentence typed again in another provider all
/// build and pass every behaviour test, and each drifts from its twin later. The rules are in
/// <see cref="CodeRuleBook"/>; their reasons are in the memory note <i>Deguffer code rules the
/// source sweep enforces</i>.</para>
/// </summary>
public sealed class CodeRuleSweepTests
{
    public static TheoryData<string> Rules => [.. CodeRuleBook.All.Select(rule => rule.Name)];

    [Theory]
    [MemberData(nameof(Rules))]
    public void NoRuleGrowsPastItsBaseline(string name)
    {
        var rule = CodeRuleBook.All.Single(rule => rule.Name == name);

        var found = ProductFiles.Value
            .Where(file => rule.HoldsIn(file.Path))
            .SelectMany(file => rule.ViolationsIn(file.Path, SourceText.Mask(file.Text, rule.View)));

        RuleBaseline.Hold($"{name}. {rule.Instead} The baseline is Baselines/{name}.txt.", found, Baseline.Read(name));
    }

    /// <summary>
    /// A rule with an empty baseline passes whatever the source holds, so a pattern that stopped
    /// matching would pass in silence. Each must still find the breach it names.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rules))]
    public void EveryRuleFindsTheBreachItNames(string name)
    {
        var rule = CodeRuleBook.All.Single(rule => rule.Name == name);

        Assert.Equal(1, rule.UsesIn(SourceText.Mask(rule.Proof, rule.View)));
    }

    [Theory]
    [MemberData(nameof(Rules))]
    public void NoCodeRuleReadsACommentThatNamesItsBreach(string name)
    {
        var rule = CodeRuleBook.All.Single(rule => rule.Name == name);

        if (rule.View == SourceView.Everything)
        {
            return;
        }

        Assert.Equal(0, rule.UsesIn(SourceText.Mask($"// {rule.Proof}\n/* {rule.Proof} */\n", rule.View)));
    }

    /// <summary>
    /// Code that only resembles a breach: a guard before a selection, a call's result, a member that
    /// returns a step, a handler, a filtered catch. A pattern that counted these would grow a baseline
    /// with uses nobody can fix.
    /// </summary>
    [Theory]
    [InlineData("selection-is-not-an-enum-ordinal", "if (reset) OrderBox.SelectedIndex = 0;")]
    [InlineData("selection-is-not-an-enum-ordinal", "var index = Find(name).SelectedIndex;")]
    [InlineData("step-kind-is-decided-by-the-step", "private static DeleteStep Template => new(\"x\", \"y\");")]
    [InlineData("async-void-only-for-events", "private async void OnOpened(object sender, object e)")]
    [InlineData("catch-says-what-it-expects", "catch (IOException ex) when (Expected(ex))")]
    public void NoRuleCountsWhatOnlyResemblesItsBreach(string name, string code)
    {
        var rule = CodeRuleBook.All.Single(rule => rule.Name == name);

        Assert.Equal(0, rule.UsesIn(SourceText.Mask(code, rule.View)));
    }

    [Theory]
    [InlineData("step-kind-is-decided-by-the-step", "RunCommandStep command when command.Scheduled is { } => Run(command),")]
    [InlineData("step-kind-is-decided-by-the-step", "    DeleteStep => Delete(),")]
    [InlineData("selection-is-not-an-enum-ordinal", "return (RowOrder)OrderBox.SelectedIndex;")]
    public void EveryRuleFindsEachFormOfItsBreach(string name, string code)
    {
        var rule = CodeRuleBook.All.Single(rule => rule.Name == name);

        Assert.Equal(1, rule.UsesIn(SourceText.Mask(code, rule.View)));
    }

    [Fact]
    public void EveryBaselineBelongsToARule()
    {
        var known = CodeRuleBook.All.Select(rule => rule.Name)
            .Append(FileLengthTests.BaselineName)
            .Append(NativeEntryPointTests.BaselineName);

        Assert.Empty(Baseline.Names().Except(known, StringComparer.Ordinal));
    }

    /// <summary>Every product C# file, by repository-relative path with forward slashes.</summary>
    internal static readonly Lazy<IReadOnlyList<(string Path, string Text)>> ProductFiles = new(() =>
        [.. ProductSources.Files()
            .Select(file => (
                Path.GetRelativePath(MarkdownGuide.RepositoryRoot, file).Replace('\\', '/'),
                File.ReadAllText(file)))]);
}
