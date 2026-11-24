using System.Text.RegularExpressions;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Every dialog defaults to its safe button, so Enter never confirms a deletion the user has not
/// read, and every dialog is built by the shell's one factory, <c>DialogFactory</c>. A dialog built
/// by hand has to repeat the root, the theme and the default button, and one that forgets any of
/// them has nowhere to open, renders in the wrong theme or confirms on Enter, which nothing else
/// catches. Its wording comes from a Core prompt, where a test can hold it.
///
/// <para>NN/g on confirmation dialogs, which outranks Fluent's samples here: never make the
/// destructive answer the default.</para>
/// </summary>
public sealed partial class DesignDialogTests
{
    private const string Factory = "Deguffer.App/Shell/DialogFactory.cs";

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
        var found = AppSources()
            .Where(file => file.Name != Factory)
            .SelectMany(file => file.Code
                .Where(code => Construction().IsMatch(code.Line))
                .Select(code => new RuleViolation(file.Name, code.Number, "builds a ContentDialog by hand")));

        RuleBaseline.Hold($"a ContentDialog is built by {Factory}", found, new Dictionary<string, int>());
    }

    private static IEnumerable<(string Name, IReadOnlyList<(int Number, string Line)> Code)> AppSources() =>
        TestProjects.SourceFiles(Path.Combine(SourceTree.RepositoryRoot, "Deguffer.App"))
            .Select(file => (
                Path.GetRelativePath(SourceTree.RepositoryRoot, file).Replace('\\', '/'),
                (IReadOnlyList<(int Number, string Line)>)ProductSources.NumberedCode(File.ReadAllText(file).ReplaceLineEndings("\n")).ToList()));

    // A qualified name and a target-typed new() are both ways to build one that a bare match misses.
    [GeneratedRegex(@"\bnew\s+(?:global::)?(?:[\w.]+\.)?ContentDialog\b|\bContentDialog\s+\w+\s*=\s*new\s*\(")]
    private static partial Regex Construction();

    [GeneratedRegex(@"DefaultButton\s*=\s*ContentDialogButton\.Close\b")]
    private static partial Regex SafeDefault();
}
