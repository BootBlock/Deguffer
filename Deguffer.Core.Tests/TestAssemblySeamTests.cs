using System.Text.RegularExpressions;

namespace Deguffer.Core.Tests;

/// <summary>
/// No test holds a real route that hands Windows a whole volume or a cloud account, except where it
/// asks something that cannot change the machine.
///
/// <para><b>Read from the source, because the danger is a test that compiles.</b> The providers and
/// the executor no longer default to these routes, so a test gets one only by naming it or by building
/// the app's own provider list through <c>CleanupPlanner.CreateDefault</c>. This catches the name. The real Recycle Bin route empties the bin of whoever runs the suite, and the real Disk
/// Cleanup host can delete the previous Windows installation. Either would sit one broken guard away
/// from doing that.</para>
/// </summary>
public sealed partial class TestAssemblySeamTests
{
    /// <summary>
    /// Every use a test may make, with how many times the file makes it, so a new use in an allowed
    /// file fails as well as a use anywhere else.
    ///
    /// <list type="bullet">
    /// <item><c>WindowsServicingTests</c> asks the real Disk Cleanup host to run and to survey a handler
    /// Windows never registers, so a broken guard reaches a registry lookup that finds nothing.</item>
    /// <item><c>CloudFilesTests</c> acts only on a sync root it registers in its own scratch
    /// folder.</item>
    /// </list>
    /// </summary>
    private static readonly SortedDictionary<string, int> Allowed = new(StringComparer.Ordinal)
    {
        ["Deguffer.Core.Tests/CloudFilesTests.cs: CloudFiles"] = 1,
        ["Deguffer.Core.Tests/WindowsServicingTests.cs: DiskCleanupHandlers"] = 2,
    };

    [Fact]
    public void NoTestHoldsARealDestructiveRouteOutsideTheAllowedCases()
    {
        var found = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (var file in TestSources())
        {
            var name = Path.GetRelativePath(MarkdownGuide.RepositoryRoot, file).Replace('\\', '/');

            foreach (Match use in RealRoute().Matches(File.ReadAllText(file)))
            {
                var key = $"{name}: {use.Groups["type"].Value}";
                found[key] = found.GetValueOrDefault(key) + 1;
            }
        }

        Assert.Equal(Allowed, found);
    }

    /// <summary>
    /// Every C# file in a test project and in the shared test library, found by name so a test project
    /// added later is covered the moment it exists.
    /// </summary>
    private static IEnumerable<string> TestSources() =>
        Directory.EnumerateDirectories(MarkdownGuide.RepositoryRoot)
            .Where(project => Path.GetFileName(project) is { } name
                && (name.EndsWith(".Tests", StringComparison.Ordinal) || name == "Deguffer.Testing"))
            .SelectMany(project => Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
            .Where(file => !IsBuildOutput(file));

    private static bool IsBuildOutput(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\b(?<type>ShellRecycleBinEmptier|DiskCleanupHandlers|CloudFiles)\s*\.\s*Default\b")]
    private static partial Regex RealRoute();
}
