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
/// to be ended is its child. The child writes its process id, so the test can hold it.</para>
/// </summary>
public sealed class ToolLifetimeTests : IDisposable
{
    private static readonly string PowerShell = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>Far longer than any test waits, so a child still running at the end was not ended.</summary>
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
        var (shim, pidFile, _) = Shim(seconds: ChildSeconds);
        using var cts = new CancellationTokenSource();

        var run = Runner().RunAsync(shim, string.Empty, ToolRun.Query, cts.Token);
        var child = await ChildAsync(pidFile, run);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        // Exited rather than exiting: the next scan starts as soon as this returns, and a child still
        // at work would run beside the one that scan starts.
        Assert.True(child.WaitForExit(TimeSpan.Zero) || child.WaitForExit(ExitWait), "The tool the shim started was still running.");
    }

    [Fact]
    public async Task AQueryPastItsLimitIsEndedWithTheToolItStartedAndFailsSayingSo()
    {
        var (shim, pidFile, _) = Shim(seconds: ChildSeconds);
        var limit = TimeSpan.FromSeconds(15);
        var started = Stopwatch.StartNew();

        var run = Runner().RunAsync(shim, string.Empty, ToolRun.QueryWithin(limit), CancellationToken.None);
        var child = await ChildAsync(pidFile, run);
        var outcome = await run;

        Assert.Equal(-1, outcome.ExitCode);
        Assert.Equal("tool.cmd did not finish within 15 seconds, so Deguffer ended it.", outcome.StandardError);
        Assert.InRange(started.Elapsed, limit, limit + TimeSpan.FromSeconds(ChildSeconds / 2));
        Assert.True(child.WaitForExit(ExitWait), "The tool the shim started outlived its limit.");
    }

    [Fact]
    public async Task ACommandLeftToFinishIsNotEndedByTheCancellation()
    {
        var (shim, pidFile, finished) = Shim(seconds: 3);
        using var cts = new CancellationTokenSource();

        var run = Runner().RunAsync(shim, string.Empty, ToolRun.Command(CancelledCommand.Finishes), cts.Token);
        await ChildAsync(pidFile, run);

        await cts.CancelAsync();
        var outcome = await run;

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.True(File.Exists(finished), "The command was stopped before it finished.");
    }

    [Fact]
    public async Task ClosingTheLifetimeJobEndsATreeThatMayBeEnded()
    {
        // What Windows does to the shared job when Deguffer exits, however it exits.
        var lifetime = ToolJob.Create(killOnClose: true);
        var (shim, pidFile, _) = Shim(seconds: ChildSeconds);

        var run = Runner(() => lifetime).RunAsync(shim, string.Empty, ToolRun.Command(CancelledCommand.Ended), CancellationToken.None);
        var child = await ChildAsync(pidFile, run);

        lifetime.Dispose();

        Assert.True(child.WaitForExit(ExitWait), "A tool outlived the job that stands for Deguffer's life.");
        await run.WaitAsync(ExitWait);
    }

    [Fact]
    public async Task ClosingTheLifetimeJobLeavesACommandThatFinishes()
    {
        var lifetime = ToolJob.Create(killOnClose: true);
        var (shim, pidFile, finished) = Shim(seconds: 3);

        var run = Runner(() => lifetime).RunAsync(shim, string.Empty, ToolRun.Command(CancelledCommand.Finishes), CancellationToken.None);
        await ChildAsync(pidFile, run);

        lifetime.Dispose();
        var outcome = await run;

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.True(File.Exists(finished), "Closing Deguffer stopped a command that must be left to finish.");
    }

    [Fact]
    public async Task ARunCancelledBeforeItStartsNeverStartsTheTool()
    {
        var (shim, pidFile, _) = Shim(seconds: 0);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Runner().RunAsync(shim, string.Empty, ToolRun.Command(CancelledCommand.Finishes), cts.Token));

        Assert.False(File.Exists(pidFile), "A tool was started for a run already cancelled.");
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
    /// A shim that starts a PowerShell, which writes its process id, waits <paramref name="seconds"/>
    /// and then writes the finished marker.
    /// </summary>
    private (string Shim, string PidFile, string Finished) Shim(int seconds)
    {
        var folder = _temp.CreateDirectory("shim-" + Guid.NewGuid().ToString("N"));
        var pidFile = Path.Combine(folder, "child.pid");
        var finished = Path.Combine(folder, "finished");
        var shim = Path.Combine(folder, "tool.cmd");

        File.WriteAllText(
            shim,
            $"@\"{PowerShell}\" -NoProfile -NonInteractive -Command \"[IO.File]::WriteAllText('{pidFile}', $PID); "
            + $"Start-Sleep -Seconds {seconds}; [IO.File]::WriteAllText('{finished}', 'done')\"\r\n");

        return (shim, pidFile, finished);
    }

    /// <summary>The PowerShell the shim started, once it has said who it is.</summary>
    private async Task<Process> ChildAsync(string pidFile, Task run)
    {
        var waited = Stopwatch.StartNew();

        while (waited.Elapsed < StartWait)
        {
            if (run.IsCompleted)
            {
                await run;
                Assert.Fail("The tool finished before its child said who it was.");
            }

            if (File.Exists(pidFile) && int.TryParse(await ReadSharedAsync(pidFile), out var pid))
            {
                var child = Process.GetProcessById(pid);
                _children.Add(child);
                return child;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The shim's child never said who it was.");
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
}
