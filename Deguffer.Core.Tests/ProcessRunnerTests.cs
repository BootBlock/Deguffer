using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The directory every tool runs in. These start real processes, because the working directory is
/// what Windows hands the child, and only the child can say what it received.
/// </summary>
public sealed class ProcessRunnerTests : IDisposable
{
    private static readonly string Cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ToolsRunInAFolderDegufferOwnsUnderLocalAppData()
    {
        var environment = new FakeUserEnvironment(_temp.CreateDirectory("machine"));

        Assert.Equal(
            Path.Combine(environment.LocalAppData, "Deguffer", "tool-working-directory"),
            ProcessRunner.WorkingDirectoryFor(environment));
    }

    [Fact]
    public void TheSharedRunnerUsesThatFolderForTheCurrentUser() =>
        Assert.Equal(ProcessRunner.WorkingDirectoryFor(UserEnvironment.Current), ProcessRunner.Default.WorkingDirectory);

    [Fact]
    public async Task AProgramRunsInTheRunnersFolderAndNotInDegufferOwn()
    {
        var folder = Path.Combine(_temp.Path, "not yet made");
        Assert.NotEqual(folder, Environment.CurrentDirectory, StringComparer.OrdinalIgnoreCase);

        var outcome = await new ProcessRunner(folder).RunAsync(Cmd, "/d /c cd", CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(folder, outcome.StandardOutput.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ABatchShimRunsInTheRunnersFolderToo()
    {
        // npm, pnpm and the other Node tools are .cmd shims, started through cmd.exe rather than
        // directly, which is a second route to the same directory.
        var shim = Path.Combine(_temp.CreateDirectory("bin"), "tool.cmd");
        File.WriteAllText(shim, "@echo %CD%\r\n");
        var folder = Path.Combine(_temp.Path, "tools");

        var outcome = await new ProcessRunner(folder).RunAsync(shim, string.Empty, CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(folder, outcome.StandardOutput.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AFolderThatCannotBeMadeFailsTheStepWithoutRunningTheTool()
    {
        var marker = Path.Combine(_temp.Path, "ran");
        var shim = Path.Combine(_temp.CreateDirectory("bin"), "tool.cmd");
        File.WriteAllText(shim, $"@echo ran> \"{marker}\"\r\n");

        // A file where the folder belongs, so the folder cannot be made.
        var occupied = _temp.CreateFile(1, "occupied");

        var folder = Path.Combine(occupied, "tools");
        var refusal = Assert.Throws<IOException>(() => Directory.CreateDirectory(LongPath.Extended(folder)));

        var outcome = await new ProcessRunner(folder).RunAsync(shim, string.Empty, CancellationToken.None);

        Assert.Equal(-1, outcome.ExitCode);
        Assert.False(File.Exists(marker), "The tool ran in a directory other than the one it was given.");

        // The reason is the folder's, not the "directory name is invalid" a launch into a missing
        // folder would give, so the step says what actually stopped it.
        Assert.Equal(refusal.Message, outcome.StandardError);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"Deguffer\tool-working-directory")]
    public async Task AFolderThatIsNotAFullPathFailsTheStepWithoutRunningTheTool(string folder)
    {
        // What WorkingDirectoryFor composes from the empty LocalAppData Windows gives where no profile
        // is loaded. Resolved, it would be a folder under Deguffer's own working directory.
        var marker = Path.Combine(_temp.Path, "ran");
        var shim = Path.Combine(_temp.CreateDirectory("bin"), "tool.cmd");
        File.WriteAllText(shim, $"@echo ran> \"{marker}\"\r\n");

        var outcome = await new ProcessRunner(folder).RunAsync(shim, string.Empty, CancellationToken.None);

        Assert.Equal(-1, outcome.ExitCode);
        Assert.False(File.Exists(marker), "The tool ran in a folder resolved against Deguffer's own directory.");
        Assert.False(folder.Length > 0 && Directory.Exists(Path.GetFullPath(folder)), "A relative folder was made under Deguffer's own directory.");
    }
}
