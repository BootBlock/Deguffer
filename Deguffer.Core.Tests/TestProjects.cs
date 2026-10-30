namespace Deguffer.Core.Tests;

/// <summary>
/// The repository's test projects, found by name so a test project added later is covered the moment
/// it exists, for a test that reads what every test project holds.
/// </summary>
internal static class TestProjects
{
    /// <summary>Every project whose name ends <c>.Tests</c>.</summary>
    public static IEnumerable<string> Directories() =>
        Directory.EnumerateDirectories(MarkdownGuide.RepositoryRoot)
            .Where(project => Path.GetFileName(project).EndsWith(".Tests", StringComparison.Ordinal));

    /// <summary>The C# files of <paramref name="project"/>, leaving out what a build wrote.</summary>
    public static IEnumerable<string> SourceFiles(string project) =>
        Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories).Where(file => !IsBuildOutput(file));

    private static bool IsBuildOutput(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
}
