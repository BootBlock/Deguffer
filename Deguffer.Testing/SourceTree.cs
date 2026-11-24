namespace Deguffer.Testing;

/// <summary>
/// The repository's own tree, for a test that holds the source, the markup or a project file to a
/// rule. Read from the tree rather than from a copy in the test output, so the test asserts against
/// the file that ships.
/// </summary>
public static class SourceTree
{
    private static readonly Lazy<string> Root = new(FindRoot);

    /// <summary>The directory that holds <c>Deguffer.sln</c>.</summary>
    public static string RepositoryRoot => Root.Value;

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Deguffer.sln")))
        {
            directory = directory.Parent;
        }

        Assert.True(
            directory is not null,
            $"No directory above {AppContext.BaseDirectory} contains Deguffer.sln, so the "
                + "repository root could not be found.");

        return directory!.FullName;
    }
}
