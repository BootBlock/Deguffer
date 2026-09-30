using Deguffer.Core.Providers;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which projects a solution names, and the difference between a solution that names none of them
/// and one that could not be read. The first lets a project's build output go while an editor has
/// the solution open. The second must not.
/// </summary>
public sealed class SolutionFileTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// Both forms, each written as Visual Studio writes it: a project in a subfolder, one reached by
    /// climbing out of the solution's folder, and, in a <c>.sln</c>, a solution folder listed in the
    /// same form as a project, which is not a folder a build writes into.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NamesTheFolderOfEveryProjectItHolds(bool xml)
    {
        var solutionFolder = _temp.CreateDirectory("repo", "App");
        var inside = Path.Combine(solutionFolder, "src", "App.Core");
        var beside = Path.Combine(_temp.Path, "repo", "Shared");

        var solution = ProjectFixture.CreateSolution(
            solutionFolder,
            "App",
            xml,
            Path.Combine(inside, "App.Core.csproj"),
            Path.Combine(beside, "Shared.fsproj"));

        Assert.Equal(
            [inside, beside],
            SolutionFile.ProjectFolders(solution)!.Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A file named like a solution that is not one reads as "could not tell", not as a solution
    /// naming nothing. A <c>.sln</c> saved in another encoding is the ordinary way to get one.
    /// </summary>
    [Theory]
    [InlineData("App.sln", "Project(\"{9A19103F-16F7-4668-BE54-9A1E7A4F7556}\") = \"A\", \"A\\A.csproj\", \"{0}\"")]
    [InlineData("App.slnx", "<Solution><Project Path=\"A/A.csproj\"")]
    [InlineData("App.slnx", "<Project Path=\"A/A.csproj\" />")]
    public void AFileThatIsNotASolutionIsUnreadableRatherThanEmpty(string name, string content)
    {
        var solution = Path.Combine(_temp.CreateDirectory("App"), name);
        File.WriteAllText(solution, content);

        Assert.Null(SolutionFile.ProjectFolders(solution));
    }

    [Fact]
    public void ASolutionThatIsNotThereIsUnreadable() =>
        Assert.Null(SolutionFile.ProjectFolders(Path.Combine(_temp.Path, "Missing.sln")));

    [Fact]
    public void ASolutionWithNoProjectsNamesNone()
    {
        var solution = ProjectFixture.CreateSolution(_temp.CreateDirectory("Empty"), "Empty", xml: false);

        Assert.Empty(SolutionFile.ProjectFolders(solution)!);
    }
}
