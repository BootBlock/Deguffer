using System.Diagnostics;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Benchmark.Tests;

/// <summary>
/// The command run before each part of the race runs as typed and is waited for, one that fails
/// stops the run rather than leaving a part measured as though it had done its work, and cancelling
/// the run ends it.
/// </summary>
public sealed class PartCommandTests : IDisposable
{
    private static readonly string Shell = NativeSystemTool.In(SystemDirectories.Current, "cmd.exe");

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// The file exists when the call returns, so the command ran, and ran to its end, before the part
    /// it comes before. The quotes around the path reach cmd.exe as typed.
    /// </summary>
    [Fact]
    public void RunsTheCommandAsTypedAndWaitsForIt()
    {
        var marker = Path.Combine(_temp.CreateDirectory("a folder"), "ran.txt");

        Command($"echo ran> \"{marker}\"").Run(CancellationToken.None);

        Assert.Equal("ran", File.ReadAllText(marker).Trim());
    }

    [Fact]
    public void ACommandThatFailsStopsTheRun()
    {
        var failed = Assert.Throws<PartCommandFailedException>(() => Command("exit 3").Run(CancellationToken.None));

        Assert.Contains("exit code 3", failed.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A command that would take a minute is ended when the run is cancelled, so Ctrl+C during a long
    /// cache-emptying command stops the benchmark rather than waiting it out.
    /// </summary>
    [Fact]
    public void CancellingTheRunEndsTheCommand()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var clock = Stopwatch.StartNew();

        Assert.ThrowsAny<OperationCanceledException>(() => Command("ping -n 60 127.0.0.1 > nul").Run(cancel.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"The command ran for {clock.Elapsed} after the run was cancelled.");
    }

    private PartCommand Command(string command) => new(new ProcessRunner(_temp.CreateDirectory("work")), Shell, command);
}
