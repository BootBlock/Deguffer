using System.Text.RegularExpressions;

namespace Deguffer.Core.Tests;

/// <summary>
/// The words the app shows follow one set of conventions, held over the string literals of the shell
/// and of the Core that words its sentences:
///
/// <list type="bullet">
/// <item>A count is written with its real plural, never "file(s)": "1 folder(s) left in place" is
/// wrong in the singular and reads as unfinished.</item>
/// <item>A failure is worded for a person. An exception's own message is written for a developer
/// ("Value cannot be null. (Parameter 'path')") and says neither whether anything was removed nor
/// what to do next, so the shell never interpolates one into what it shows.</item>
/// <item>A size is in the units Windows itself shows, KB, MB and GB, never KiB or GiB beside them.</item>
/// <item>A date follows the user's culture through one formatter, never a fixed pattern such as
/// <c>d MMM yyyy</c> that puts the day first for a reader who expects the month first.</item>
/// </list>
///
/// <para>Nielsen's heuristics 4 (consistency) and 9 (error recovery). Each is
/// a ratchet: a file's count of violations may only go down. Some counted sites are deliberate, such
/// as a crash log or a file name, and stay in the baseline for that reason.</para>
/// </summary>
public sealed partial class DesignUserTextTests
{
    private static readonly Dictionary<string, int> PluralBaseline = new(StringComparer.Ordinal)
    {
        ["Deguffer.App/ViewModels/ExploreSelection.cs"] = 1,
        ["Deguffer.Core/Duplicates/DuplicateRemovalReport.cs"] = 1,
        ["Deguffer.Core/Execution/CleanupResult.cs"] = 2,
        ["Deguffer.Core/Execution/LeftInPlace.cs"] = 11,
        ["Deguffer.Core/Execution/LocalCopyRelease.cs"] = 4,
        ["Deguffer.Core/Execution/PlanExecutor.cs"] = 1,
        ["Deguffer.Core/Execution/RecordedRefusals.cs"] = 2,
        ["Deguffer.Core/Execution/RunOutcome.cs"] = 9,
        ["Deguffer.Core/Execution/SiblingCheck.cs"] = 2,
        ["Deguffer.Core/Exploring/Acting/ExploreRemover.cs"] = 1,
        ["Deguffer.Core/Memory/Acting/CloseReport.cs"] = 1,
        ["Deguffer.Core/Providers/CloudLocalCopiesProvider.cs"] = 2,
        ["Deguffer.Core/Providers/TemporaryUserWork.cs"] = 1,
    };

    private static readonly Dictionary<string, int> ExceptionTextBaseline = new(StringComparer.Ordinal)
    {
        ["Deguffer.App/ViewModels/CleanViewModel.cs"] = 2,
        ["Deguffer.App/ViewModels/ExploreSelection.cs"] = 1,
        ["Deguffer.App/ViewModels/ExploreShortcutFolders.cs"] = 1,
        ["Deguffer.App/ViewModels/ExploreViewModel.cs"] = 1,
        ["Deguffer.App/ViewModels/MemoryViewModel.cs"] = 1,
    };

    private static readonly Dictionary<string, int> UnitBaseline = new(StringComparer.Ordinal)
    {
        ["Deguffer.App/Views/SettingsPage.xaml"] = 4,
        ["Deguffer.Core/Providers/CcacheProvider.cs"] = 1,
        ["Deguffer.Core/Providers/ComposerCacheProvider.cs"] = 1,
        ["Deguffer.Core/Providers/SccacheProvider.cs"] = 1,
        ["Deguffer.Core/Scanning/ScanTuningText.cs"] = 2,
    };

    private static readonly Dictionary<string, int> DatePatternBaseline = new(StringComparer.Ordinal)
    {
        ["Deguffer.App/ViewModels/DuplicateCopyRow.cs"] = 1,
        ["Deguffer.Core/Diagnostics/CrashLog.cs"] = 1,
        ["Deguffer.Core/Exploring/MapCaption.cs"] = 1,
        ["Deguffer.Core/InstalledApps/RegistryBackups.cs"] = 1,
        ["Deguffer.Core/Profiles/ProfileReport.cs"] = 1,
        ["Deguffer.Core/Providers/ClaudeCodeConversationOffers.cs"] = 1,
        ["Deguffer.Core/Providers/DriverPackageNotes.cs"] = 1,
        ["Deguffer.Core/Providers/DriverStoreProvider.cs"] = 1,
        ["Deguffer.Core/SystemProtection/RestorePoint.cs"] = 1,
        ["Deguffer.Core/SystemProtection/ShadowCopy.cs"] = 1,
    };

    [Fact]
    public void CountsAreWrittenWithTheirRealPlural()
    {
        RuleBaseline.Hold("no \"(s)\" plural in a string literal", InLiterals(ShownSources(), Plural(), "writes a plural as \"(s)\""), PluralBaseline);
    }

    [Fact]
    public void TheShellNeverShowsAnExceptionsOwnMessage()
    {
        var app = TestProjects.SourceFiles(Path.Combine(MarkdownGuide.RepositoryRoot, "Deguffer.App"));

        RuleBaseline.Hold("no exception Message interpolated into text the shell shows", InLiterals(app, ExceptionMessage(), "shows an exception's own message"), ExceptionTextBaseline);
    }

    [Fact]
    public void SizesUseTheUnitsWindowsShows()
    {
        var markup = ShellMarkup.Files.SelectMany(file => file.Elements
            .SelectMany(element => element.Attributes())
            .Where(attribute => BinaryUnit().IsMatch(attribute.Value))
            .Select(attribute => file.At(attribute, $"writes the unit in \"{attribute.Value}\"")));

        RuleBaseline.Hold(
            "sizes are KB, MB and GB, never KiB, MiB, GiB or TiB",
            InLiterals(ShownSources(), BinaryUnit(), "writes a KiB-style unit").Concat(markup),
            UnitBaseline);
    }

    [Fact]
    public void DatesFollowTheUsersCulture()
    {
        var found = ShownSources().SelectMany(file => Lines(file)
            .Where(code => DatePattern().IsMatch(code.Line))
            .Select(code => new RuleViolation(code.File, code.Number, "formats a date with a fixed pattern")));

        RuleBaseline.Hold("a date is formatted through the culture, never a fixed pattern", found, DatePatternBaseline);
    }

    /// <summary>The projects whose words reach a user: the shell, and the Core that words its sentences.</summary>
    private static IEnumerable<string> ShownSources() =>
        ShellSources().Concat(TestProjects.SourceFiles(Path.Combine(MarkdownGuide.RepositoryRoot, "Deguffer.Core")));

    private static IEnumerable<string> ShellSources() =>
        TestProjects.SourceFiles(Path.Combine(MarkdownGuide.RepositoryRoot, "Deguffer.App"));

    private static IEnumerable<RuleViolation> InLiterals(IEnumerable<string> files, Regex pattern, string detail) =>
        files.SelectMany(file => Lines(file)
            .SelectMany(code => StringLiteral().Matches(code.Line)
                .Where(literal => pattern.IsMatch(literal.Value))
                .Select(_ => new RuleViolation(code.File, code.Number, detail))));

    private static IEnumerable<(string File, int Number, string Line)> Lines(string file)
    {
        var name = Path.GetRelativePath(MarkdownGuide.RepositoryRoot, file).Replace('\\', '/');

        return ProductSources.NumberedCode(File.ReadAllText(file).ReplaceLineEndings("\n"))
            .Select(code => (name, code.Number, code.Line));
    }

    [GeneratedRegex(@"\$?@?""(?:[^""\\\n]|\\.)*""")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"\w\(s\)")]
    private static partial Regex Plural();

    [GeneratedRegex(@"\{\w+\.Message\}")]
    private static partial Regex ExceptionMessage();

    [GeneratedRegex(@"\b[KMGT]iB\b")]
    private static partial Regex BinaryUnit();

    [GeneratedRegex(@"ToString\(\s*""[^""]*(?:yyyy|MMM|HH:mm|dd/MM|MM/dd)|\{[^{}:""]+:[^{}""]*(?:yyyy|MMM|HH:mm)[^{}""]*\}")]
    private static partial Regex DatePattern();
}
