using System.Text.RegularExpressions;

namespace Deguffer.Core.Tests;

/// <summary>
/// Every dialog defaults to its safe button, so Enter never confirms a deletion the user has not
/// read, and every dialog is built in one place. The shell held the first rule everywhere when it
/// was written, so it has no exceptions. The second is a ratchet: each dialog built by hand repeats
/// the root, the theme and the default button, and a wording decided in code-behind is one no test
/// can hold to its title.
///
/// <para>NN/g on confirmation dialogs, which outranks Fluent's samples here: never make the
/// destructive answer the default.</para>
/// </summary>
public sealed partial class DesignDialogTests
{
    private static readonly Dictionary<string, int> ConstructionBaseline = new(StringComparer.Ordinal)
    {
        ["Deguffer.App/Shell/ContentDialogClosePrompt.cs"] = 1,
        ["Deguffer.App/Shell/ContentDialogCompletionCountdown.cs"] = 1,
        ["Deguffer.App/Shell/ContentDialogConfirmationPrompt.cs"] = 1,
        ["Deguffer.App/Shell/ContentDialogDuplicateConfirmation.cs"] = 1,
        ["Deguffer.App/Shell/ContentDialogExploreConfirmation.cs"] = 1,
        ["Deguffer.App/Shell/ContentDialogInstalledAppsConfirmation.cs"] = 1,
        ["Deguffer.App/Shell/IMemoryConfirmationPrompt.cs"] = 1,
        ["Deguffer.App/Views/CleanPage.xaml.cs"] = 2,
        ["Deguffer.App/Views/SettingsPage.xaml.cs"] = 1,
    };

    [Fact]
    public void EveryDialogDefaultsToItsSafeButton()
    {
        var unsafeDialogs = AppSources()
            .Select(file => (file.Name, Built: file.Code.Count(code => Construction().IsMatch(code.Line)), Safe: file.Code.Count(code => SafeDefault().IsMatch(code.Line))))
            .Where(file => file.Built != file.Safe)
            .Select(file => $"{file.Name} builds {file.Built} dialogs and defaults {file.Safe} to the close button")
            .ToList();

        Assert.True(unsafeDialogs.Count == 0, string.Join("; ", unsafeDialogs) + ". Every ContentDialog sets DefaultButton = ContentDialogButton.Close.");
    }

    [Fact]
    public void DialogsAreBuiltInOnePlace()
    {
        var found = AppSources().SelectMany(file => file.Code
            .Where(code => Construction().IsMatch(code.Line))
            .Select(code => new DesignViolation(file.Name, code.Number, "builds a ContentDialog by hand")));

        DesignBaseline.Hold("a ContentDialog is built by the shell's one dialog factory", found, ConstructionBaseline);
    }

    private static IEnumerable<(string Name, IReadOnlyList<(int Number, string Line)> Code)> AppSources() =>
        TestProjects.SourceFiles(Path.Combine(MarkdownGuide.RepositoryRoot, "Deguffer.App"))
            .Select(file => (
                Path.GetRelativePath(MarkdownGuide.RepositoryRoot, file).Replace('\\', '/'),
                (IReadOnlyList<(int Number, string Line)>)ProductSources.NumberedCode(File.ReadAllText(file).ReplaceLineEndings("\n")).ToList()));

    // A qualified name and a target-typed new() are both ways to build one that a bare match misses.
    [GeneratedRegex(@"\bnew\s+(?:global::)?(?:[\w.]+\.)?ContentDialog\b|\bContentDialog\s+\w+\s*=\s*new\s*\(")]
    private static partial Regex Construction();

    [GeneratedRegex(@"DefaultButton\s*=\s*ContentDialogButton\.Close\b")]
    private static partial Regex SafeDefault();
}
