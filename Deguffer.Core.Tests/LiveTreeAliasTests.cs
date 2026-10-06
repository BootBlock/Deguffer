using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A program reports its paths the way it was started, so the in-use check follows each one, and each
/// directory asked about, to every path it is reachable at. A program working through a letter
/// <c>subst</c> made, or through another mount of the volume, is using the folder it is in.
/// </summary>
public sealed class LiveTreeAliasTests
{
    private const string Source = @"C:\Users\testuser\src";
    private const string Project = Source + @"\app";

    private static readonly LiveTreeQuery Build = new(Project + @"\bin", Project);

    private readonly FakeVolumeInventory _volumes = new FakeVolumeInventory().Substituting(@"S:\", Source);

    /// <summary>
    /// A shell working in the project through <c>S:</c> makes its build output live, and so does one
    /// at the top of a letter standing for the project. One working in a sibling reached the same
    /// way does not (§5.6).
    /// </summary>
    [Theory]
    [InlineData(@"S:\app\", true)]
    [InlineData(@"S:\app\src\", true)]
    [InlineData(@"W:\", true)]
    [InlineData(@"S:\other\", false)]
    [InlineData(@"S:\", false)]
    public void AProgramWorkingThroughASubstitutedLetterIsWorkingInTheFolderItStandsFor(string working, bool live)
    {
        _volumes.Substituting(@"W:\", Project);

        var findings = Inspector(Working("cmd", working)).FindLive([Build]);

        Assert.True(findings.Complete);
        Assert.Equal(live, findings.IsLive(Build.Directory));
    }

    /// <summary>
    /// A program running from inside the build output through another mount of the system volume is
    /// running from inside it. One running from beside it, mounted the same way, is not (§5.6).
    /// </summary>
    [Theory]
    [InlineData(@"Q:\SysMount\Users\testuser\src\app\bin\app.exe", true)]
    [InlineData(@"Q:\SysMount\Users\testuser\src\app\tools\app.exe", false)]
    public void AProgramRunningThroughAnotherMountOfTheVolumeRunsFromWhereItIs(string image, bool live)
    {
        _volumes.With(@"C:\", alsoMountedAt: [@"Q:\SysMount\"]);

        var findings = Inspector(new FakeListedProcess { Id = 4399, Name = "app", ImagePath = image }).FindLive([Build]);

        Assert.Equal(live, findings.IsLive(Build.Directory));
    }

    /// <summary>
    /// Visual Studio working in the solution's folder through <c>S:</c> is working in that workspace,
    /// and one working in a folder beside the project, reached the same way, is not (§5.6).
    /// </summary>
    [Theory]
    [InlineData(@"S:\", true)]
    [InlineData(@"S:\other\", false)]
    public void AProgramWorkingInAWorkspaceThroughASubstitutedLetterIsWorkingThere(string working, bool live)
    {
        var query = new LiveTreeQuery(Project + @"\obj", Project) { Workspaces = [Source] };

        var findings = Inspector(Working("devenv", working)).FindLive([query]);

        Assert.Equal(live, findings.IsLive(query.Directory));
    }

    /// <summary>
    /// A program working in a scratch entry through a letter standing for the scratch folder holds
    /// that entry, named below the folder as it was asked. One at the letter's top is in the folder
    /// itself, which names no entry, and an entry nothing uses is not reported (§5.6).
    /// </summary>
    [Fact]
    public void AProgramInAScratchEntryThroughASubstitutedLetterHoldsTheEntryAsAsked()
    {
        const string scratch = @"C:\Users\testuser\AppData\Local\Temp";
        _volumes.Substituting(@"T:\", scratch);

        var findings = Inspector(
                Working("build", @"T:\run-1\obj\"),
                Working("shell", @"T:\"),
                new FakeListedProcess { Id = 4399, Name = "runner", CommandLine = @"runner.exe --log=T:\run-2\out.log" })
            .FindLiveChildren([scratch]);

        Assert.Equal(
            [Path.Combine(scratch, "run-1"), Path.Combine(scratch, "run-2")],
            findings.Live.Select(tree => tree.Directory).Order(StringComparer.OrdinalIgnoreCase));
        Assert.False(findings.IsLive(Path.Combine(scratch, "run-3")));
    }

    private LiveTreeInspector Inspector(params FakeListedProcess[] processes)
    {
        var calls = new FakeProcessTableCalls().With(FakeProcessTableCalls.Own);

        foreach (var process in processes)
        {
            calls.With(process);
        }

        return new LiveTreeInspector(calls, _volumes);
    }

    private int _nextId = 4400;

    private FakeListedProcess Working(string name, string directory) => new()
    {
        Id = ++_nextId,
        Name = name,
        CommandLine = $"{name}.exe",
        Memory = FakeProcessMemory.Native(directory, $"{name}.exe"),
    };
}
