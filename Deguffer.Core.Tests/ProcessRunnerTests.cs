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

        var outcome = await new ProcessRunner(folder).RunAsync(Cmd, "/d /c cd", ToolRun.Query, CancellationToken.None);

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

        var outcome = await new ProcessRunner(folder).RunAsync(shim, string.Empty, ToolRun.Query, CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(folder, outcome.StandardOutput.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A PATH search takes the extension's case from PATHEXT, which lists <c>.BAT</c> and <c>.CMD</c>
    /// in capitals, so Composer resolves to <c>composer.BAT</c>. A shim launched directly rather than
    /// through cmd.exe's quoting mangles a quoted argument, and every query fails.
    /// </summary>
    [Theory]
    [InlineData("tool.cmd")]
    [InlineData("tool.CMD")]
    [InlineData("tool.BAT")]
    [InlineData("tool.Bat")]
    public async Task ABatchShimOfAnyCaseReceivesAQuotedArgumentWhole(string name)
    {
        var shim = Path.Combine(_temp.CreateDirectory("bin"), name);
        File.WriteAllText(shim, "@echo %~1" + Environment.NewLine);
        var argument = Path.Combine(_temp.Path, "a folder", "with spaces");

        var outcome = await new ProcessRunner(Path.Combine(_temp.Path, "tools"))
            .RunAsync(shim, $"\"{argument}\"", ToolRun.Query, CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(argument, outcome.StandardOutput.Trim());
    }

    /// <summary>
    /// cmd.exe expands %NAME% inside a quoted argument, so a path vetted as written would reach a shim
    /// as another. The shim is not started at all.
    /// </summary>
    [Fact]
    public async Task ABatchShimIsNotStartedWithAPercentSignInItsArguments()
    {
        var marker = Path.Combine(_temp.Path, "ran");
        var shim = Path.Combine(_temp.CreateDirectory("bin"), "tool.cmd");
        File.WriteAllText(shim, $"@echo ran> \"{marker}\"" + Environment.NewLine);
        var argument = Path.Combine(_temp.Path, "%USERNAME%", "cache");

        var outcome = await new ProcessRunner(Path.Combine(_temp.Path, "tools"))
            .RunAsync(shim, $"--cache-folder \"{argument}\"", ToolRun.Query, CancellationToken.None);

        Assert.Equal(-1, outcome.ExitCode);
        Assert.False(File.Exists(marker), "The shim ran with an argument cmd.exe would have rewritten.");
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

        var outcome = await new ProcessRunner(folder).RunAsync(shim, string.Empty, ToolRun.Query, CancellationToken.None);

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

        var outcome = await new ProcessRunner(folder).RunAsync(shim, string.Empty, ToolRun.Query, CancellationToken.None);

        Assert.Equal(-1, outcome.ExitCode);
        Assert.False(File.Exists(marker), "The tool ran in a folder resolved against Deguffer's own directory.");
        Assert.False(folder.Length > 0 && Directory.Exists(Path.GetFullPath(folder)), "A relative folder was made under Deguffer's own directory.");
    }
}
