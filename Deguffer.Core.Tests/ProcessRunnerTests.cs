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

    /// <summary>
    /// A query started with an environment reads that one and nothing of Deguffer's own, which is what
    /// lets it stand for a tool the user starts now.
    /// </summary>
    [Fact]
    public async Task AProgramStartedWithAnEnvironmentReadsThatOneAndNotDegufferOwn()
    {
        // One of Deguffer's own that cmd.exe does not make for itself, as it makes PROMPT.
        const string own = "USERPROFILE";
        Assert.NotNull(Environment.GetEnvironmentVariable(own));

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot")!,
            ["DEGUFFER_STARTED_WITH"] = "given",
        };

        var outcome = await new ProcessRunner(Path.Combine(_temp.Path, "tools"))
            .RunAsync(Cmd, "/d /c set", ToolRun.Query.StartedWith(environment), CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Message);

        var names = NamesIn(outcome.StandardOutput);

        Assert.Contains("DEGUFFER_STARTED_WITH=given", outcome.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(own, names);
    }

    /// <summary>
    /// A variable a run unsets is not handed on from Deguffer's own block, whatever case it is named
    /// in, while every other variable Deguffer has still is.
    ///
    /// <para>Set on this process under a name no other test uses, so the tests running beside this one
    /// start their tools as they always did.</para>
    /// </summary>
    [Fact]
    public async Task AProgramStartedWithoutAVariableDoesNotInheritItAndInheritsTheRest()
    {
        const string inherited = "DEGUFFER_RUNNER_UNSET_TEST";
        const string own = "USERPROFILE";
        Assert.NotNull(Environment.GetEnvironmentVariable(own));
        Environment.SetEnvironmentVariable(inherited, "inherited");

        try
        {
            var outcome = await new ProcessRunner(Path.Combine(_temp.Path, "tools")).RunAsync(
                Cmd,
                "/d /c set",
                ToolRun.Query.Without(new HashSet<string> { inherited.ToLowerInvariant() }),
                CancellationToken.None);

            Assert.True(outcome.Succeeded, outcome.Message);

            var names = NamesIn(outcome.StandardOutput);

            Assert.DoesNotContain(inherited, names);
            Assert.Contains(own, names);
        }
        finally
        {
            Environment.SetEnvironmentVariable(inherited, null);
        }
    }

    /// <summary>A run given an environment of its own and told to unset a variable starts with the rest of that one.</summary>
    [Fact]
    public async Task AProgramStartedWithAnEnvironmentAndWithoutAVariableReadsTheRestOfThatOne()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot")!,
            ["DEGUFFER_KEPT"] = "kept",
            ["DEGUFFER_DROPPED"] = "dropped",
        };

        var outcome = await new ProcessRunner(Path.Combine(_temp.Path, "tools")).RunAsync(
            Cmd,
            "/d /c set",
            ToolRun.Query.StartedWith(environment).Without(new HashSet<string> { "DEGUFFER_DROPPED" }),
            CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Message);

        var names = NamesIn(outcome.StandardOutput);

        Assert.Contains("DEGUFFER_KEPT", names);
        Assert.DoesNotContain("DEGUFFER_DROPPED", names);
        Assert.DoesNotContain("USERPROFILE", names);
    }

    private static HashSet<string> NamesIn(string set) =>
        set.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line[..line.IndexOf('=', 1)])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// An environment holding a variable Windows cannot hand on whole is not passed on in part: the
    /// program is not started.
    /// </summary>
    [Fact]
    public async Task AProgramIsNotStartedWithAnEnvironmentItCannotBeGivenWhole()
    {
        var marker = Path.Combine(_temp.Path, "ran");
        var environment = new Dictionary<string, string>
        {
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot")!,
            ["ARDUINO_DIRECTORIES_DOWNLOADS"] = "C:\\staging" + '\0' + "ARDUINO_DIRECTORIES_DATA=C:\\elsewhere",
        };

        var outcome = await new ProcessRunner(Path.Combine(_temp.Path, "tools"))
            .RunAsync(Cmd, $"/d /c echo ran> \"{marker}\"", ToolRun.Query.StartedWith(environment), CancellationToken.None);

        Assert.Equal(-1, outcome.ExitCode);
        Assert.Contains("ARDUINO_DIRECTORIES_DOWNLOADS", outcome.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(marker), "The program ran with an environment it could not be given whole.");
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
