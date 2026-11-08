using System.Diagnostics;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// How long a started tool lives, and what ends it. These start real processes, because a tree of
/// processes and a job object are Windows' to keep, and only Windows can say what it ended.
///
/// <para>Each tool is a <c>.cmd</c> shim whose real work is a PowerShell beneath it, the shape of
/// every Node tool Deguffer runs: the process Deguffer starts is <c>cmd.exe</c>, and the one that has
/// to be ended is its child. The child writes its process id, so the test can hold it, and then waits
/// to be released, so it cannot finish, exit and give up its id before the test holds it.</para>
/// </summary>
public sealed class ToolLifetimeTests : IDisposable
{
    private static readonly string PowerShell = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>
    /// How long a child waits to be released before it exits without finishing. Far longer than any
    /// test waits, so a child still running at the end was not ended.
    /// </summary>
    private const int ChildSeconds = 120;

    private static readonly TimeSpan StartWait = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _temp = new();
    private readonly List<Process> _children = [];

    public void Dispose()
    {
        // A test that failed leaves its child running for two minutes, holding the scratch folder.
        foreach (var child in _children)
        {
            try
            {
                child.Kill();
            }
            catch (InvalidOperationException)
            {
                // Already exited, which is what every passing test leaves.
            }

            child.Dispose();
        }

        _temp.Dispose();
    }

    [Fact]
    public async Task ACancelledQueryEndsTheToolTheShimStartedBeforeItReturns()
    {
        var tool = Shim();
        using var cts = new CancellationTokenSource();

        var run = Runner().RunAsync(tool.Shim, string.Empty, ToolRun.Query, cts.Token);
        var child = await ChildAsync(tool, run);

        await cts.CancelAsync();

        // Bounded, because a child left running holds the shim, and the call would otherwise return,
        // late, once the child finished on its own.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(ExitWait));

        // Exited rather than exiting: the next scan starts as soon as this returns, and a child still
        // at work would run beside the one that scan starts.
        Assert.True(child.WaitForExit(TimeSpan.Zero), "The tool the shim started was still running when the call returned.");
    }

    [Fact]
    public async Task AQueryPastItsLimitIsEndedWithTheToolItStartedAndFailsSayingSo()
    {
        var tool = Shim();
        var limit = TimeSpan.FromSeconds(15);
        var started = Stopwatch.StartNew();

        var run = Runner().RunAsync(tool.Shim, string.Empty, ToolRun.QueryWithin(limit), CancellationToken.None);
        var child = await ChildAsync(tool, run);
        var outcome = await run;

        Assert.Equal(-1, outcome.ExitCode);
        Assert.Equal("tool.cmd did not finish within 15 seconds, so Deguffer ended it.", outcome.StandardError);
        Assert.InRange(started.Elapsed, limit, limit + TimeSpan.FromSeconds(ChildSeconds / 2));
        Assert.True(child.WaitForExit(ExitWait), "The tool the shim started outlived its limit.");
    }

    [Fact]
    public async Task ACommandLeftToFinishIsNotEndedByTheCancellation()
    {
        var tool = Shim();
        using var cts = new CancellationTokenSource();

        var run = Runner().RunAsync(tool.Shim, string.Empty, ToolRun.Command(CancelledCommand.Finishes), cts.Token);
        await ChildAsync(tool, run);

        // Released only after the cancellation, so the command is still at work when it comes.
        await cts.CancelAsync();
        tool.Release();
        var outcome = await run;

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.True(File.Exists(tool.Finished), "The command was stopped before it finished.");
    }

    [Fact]
    public async Task ClosingTheLifetimeJobEndsATreeThatMayBeEnded()
    {
        // What Windows does to the shared job when Deguffer exits, however it exits.
        var lifetime = ToolJob.Create(killOnClose: true);
        var tool = Shim();

        var run = Runner(() => lifetime).RunAsync(tool.Shim, string.Empty, ToolRun.Command(CancelledCommand.Ended), CancellationToken.None);
        var child = await ChildAsync(tool, run);

        lifetime.Dispose();

        Assert.True(child.WaitForExit(ExitWait), "A tool outlived the job that stands for Deguffer's life.");
        await run.WaitAsync(ExitWait);
    }

    [Fact]
    public async Task ClosingTheLifetimeJobLeavesACommandThatFinishes()
    {
        var lifetime = ToolJob.Create(killOnClose: true);
        var tool = Shim();

        var run = Runner(() => lifetime).RunAsync(tool.Shim, string.Empty, ToolRun.Command(CancelledCommand.Finishes), CancellationToken.None);
        await ChildAsync(tool, run);

        // Released only after the job is closed, so the command is still at work when it closes.
        lifetime.Dispose();
        tool.Release();
        var outcome = await run;

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.True(File.Exists(tool.Finished), "Closing Deguffer stopped a command that must be left to finish.");
    }

    [Fact]
    public async Task ARunCancelledBeforeItStartsNeverStartsTheTool()
    {
        var tool = Shim();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Runner().RunAsync(tool.Shim, string.Empty, ToolRun.Command(CancelledCommand.Finishes), cts.Token));

        Assert.False(File.Exists(tool.PidFile), "A tool was started for a run already cancelled.");
    }

    [Fact]
    public async Task AToolThatAsksAQuestionReadsTheEndOfItsInputRatherThanWaiting()
    {
        // set /p waits for a line on standard input. Given one nothing writes to, it would wait until
        // the limit and fail.
        var shim = Path.Combine(_temp.CreateDirectory("asks"), "asks.cmd");
        File.WriteAllText(shim, "@set /p answer=Continue? \r\n@echo went on\r\n");

        var outcome = await Runner().RunAsync(shim, string.Empty, ToolRun.QueryWithin(TimeSpan.FromSeconds(30)), CancellationToken.None);

        Assert.Equal("Continue? went on", outcome.StandardOutput.Trim());
    }

    [Fact]
    public async Task AProgramNamedWithoutAFullPathIsNotRun()
    {
        var outcome = await Runner().RunAsync("cmd.exe", "/d /c echo ran", ToolRun.Query, CancellationToken.None);

        Assert.Equal(-1, outcome.ExitCode);
        Assert.Equal("Not run, because 'cmd.exe' is not a full path.", outcome.StandardError);
    }

    /// <summary>
    /// Every parser of a tool's output was written against what <see cref="Process"/> handed over, so
    /// the runner hands over the same text: the same decoding of the same bytes in the same code page,
    /// a byte order mark kept as text, and the same line endings, a last line without one included.
    /// </summary>
    [Fact]
    public async Task OutputIsTheTextProcessWouldHaveGiven()
    {
        // A UTF-8 byte order mark, "A", "é" in UTF-8, a byte UTF-8 refuses, a bare LF, a CRLF, a bare
        // CR, and a last line with no ending at all.
        const string Bytes = "0xEF,0xBB,0xBF,0x41,0xC3,0xA9,0x82,0x0A,0x42,0x0D,0x0A,0x43,0x0D,0x44";
        var arguments = $"-NoProfile -NonInteractive -Command \"$o=[Console]::OpenStandardOutput(); $b=[byte[]]({Bytes}); $o.Write($b,0,$b.Length); $o.Flush()\"";

        var outcome = await Runner().RunAsync(PowerShell, arguments, ToolRun.Query, CancellationToken.None);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(PowerShell, arguments)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        var expected = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) expected.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        await process.WaitForExitAsync();

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(expected.ToString(), outcome.StandardOutput);
    }

    private ProcessRunner Runner(Func<ToolJob>? lifetime = null) =>
        lifetime is null
            ? new ProcessRunner(_temp.CreateDirectory("tools"))
            : new ProcessRunner(_temp.CreateDirectory("tools"), lifetime);

    /// <summary>
    /// A shim that starts a PowerShell, which writes its process id and waits to be released. Once
    /// released it writes the finished marker. Never released, it exits after
    /// <see cref="ChildSeconds"/> without writing it.
    /// </summary>
    private ShimTool Shim()
    {
        var folder = _temp.CreateDirectory("shim-" + Guid.NewGuid().ToString("N"));
        var tool = new ShimTool(
            Path.Combine(folder, "tool.cmd"),
            Path.Combine(folder, "child.pid"),
            Path.Combine(folder, "release"),
            Path.Combine(folder, "finished"));

        File.WriteAllText(
            tool.Shim,
            $"@\"{PowerShell}\" -NoProfile -NonInteractive -Command \"[IO.File]::WriteAllText('{tool.PidFile}', $PID); "
            + $"$until = (Get-Date).AddSeconds({ChildSeconds}); "
            + $"while (-not (Test-Path -LiteralPath '{tool.ReleaseFile}') -and ((Get-Date) -lt $until)) {{ Start-Sleep -Milliseconds 50 }}; "
            + $"if (Test-Path -LiteralPath '{tool.ReleaseFile}') {{ [IO.File]::WriteAllText('{tool.Finished}', 'done') }}\"\r\n");

        return tool;
    }

    /// <summary>
    /// The PowerShell the shim started, once it has said who it is, held open so that its id cannot
    /// pass to another process while the test has it.
    /// </summary>
    private async Task<Process> ChildAsync(ShimTool tool, Task run)
    {
        var waited = Stopwatch.StartNew();

        while (waited.Elapsed < StartWait)
        {
            if (run.IsCompleted)
            {
                await run;
                Assert.Fail("The tool finished before its child said who it was.");
            }

            if (File.Exists(tool.PidFile) && int.TryParse(await ReadSharedAsync(tool.PidFile), out var pid))
            {
                return Hold(pid, File.GetLastWriteTimeUtc(tool.PidFile));
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The shim's child never said who it was.");
    }

    /// <summary>
    /// Opens the child by its id and keeps the handle, because Windows gives no other process the id
    /// while a handle is open. A process that started after the child wrote its id is a stranger given
    /// the id once the child had gone, and must never be killed in the child's place.
    /// </summary>
    private Process Hold(int pid, DateTime idWrittenUtc)
    {
        Process child;

        try
        {
            child = Process.GetProcessById(pid);
            _ = child.SafeHandle;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // The child waits to be released, so only the run under test can have ended it this soon.
            throw new InvalidOperationException($"The shim's child ({pid}) ended before the test could hold it.", ex);
        }

        if (child.StartTime.ToUniversalTime() > idWrittenUtc)
        {
            child.Dispose();
            throw new InvalidOperationException($"The shim's child ({pid}) ended, and its id went to another process, before the test could hold it.");
        }

        _children.Add(child);
        return child;
    }

    private static async Task<string> ReadSharedAsync(string path)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync();
        }
        catch (IOException)
        {
            // Still being written.
            return string.Empty;
        }
    }

    private sealed record ShimTool(string Shim, string PidFile, string ReleaseFile, string Finished)
    {
        public void Release() => File.WriteAllText(ReleaseFile, string.Empty);
    }
}
