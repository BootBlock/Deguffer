using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// A <see cref="RunningProcessCheck"/> whose programs the run first asks to let go, with the tool's own
/// command, immediately before the removal and only while one of them runs.
///
/// <para><b>sccache is the case.</b> Its compiles go through a background server that keeps the cache's
/// index in memory, so files removed under it leave the server counting entries that are gone.
/// <c>sccache --stop-server</c> ends it cleanly, and the next compile starts a new one that reads the
/// folder afresh. The command fails when no server is running, which is why it is sent only while one
/// is: a stop with nothing to stop is not a failure of the clean.</para>
///
/// <para><b>The release is a request, and the removal does not rest on it.</b> The check is asked
/// after it as it is of any step, so a server that would not stop, or one a build started again in the
/// meantime, holds the removal back exactly as it would have without the release. One type rather than
/// a command beside a check, so a removal cannot carry the request without the check.</para>
/// </summary>
/// <param name="fileName">The tool, as the provider resolved it.</param>
/// <param name="arguments">The command that ends <paramref name="names"/>.</param>
/// <param name="names">The programs whose running holds the removal back, and the reason for the command.</param>
/// <param name="time">The clock the release waits on while the programs exit.</param>
internal sealed class ReleasingProcessCheck(
    string fileName,
    string arguments,
    IReadOnlyList<string> names,
    IProcessRunner runner,
    IProcessInspector inspector,
    TimeProvider time) : IUseCheck
{
    /// <summary>
    /// How long the programs are given to exit once the tool has answered. sccache acknowledges the stop
    /// before its server process has ended, so the first look can come too early. Long enough for a
    /// machine under load, and short enough that a server that never stops costs the run a few seconds
    /// before the check holds the removal back.
    /// </summary>
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan ExitPoll = TimeSpan.FromMilliseconds(250);

    private readonly RunningProcessCheck _running = new(inspector, names);

    private int _asked;

    public IReadOnlyList<InUseNow> Ask(DeleteStep step, CancellationToken ct) => _running.Ask(step, ct);

    /// <summary>
    /// Send the command if one of the programs runs now, and wait for them to exit. Reports nothing,
    /// because the check asked next, on a reading taken after this returns, is what the run acts on.
    ///
    /// <para><b>Once, however many removals share the check.</b> A server running again by the next
    /// removal was started by a build, and stopping it a second time would fail that build's compiles
    /// rather than free anything. The check holds the rest back instead.</para>
    /// </summary>
    public async Task ReleaseAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _asked, 1) == 1)
        {
            return;
        }

        // Read now rather than within the use checks' limit: the command is sent only on this answer, and
        // one a second old can name a server that has since stopped on its own idle timeout.
        inspector.Invalidate();

        if (inspector.FindRunning(names).Count == 0)
        {
            return;
        }

        // Ended on cancel: a stop cut short leaves the program running, which the check then sees. Its
        // outcome is not read, because the process table is what says whether it worked.
        await runner.RunAsync(fileName, arguments, ToolRun.Command(CancelledCommand.Ended), ct).ConfigureAwait(false);

        var deadline = time.GetUtcNow() + ExitWait;

        while (true)
        {
            inspector.Invalidate();

            if (inspector.FindRunning(names).Count == 0 || time.GetUtcNow() >= deadline)
            {
                return;
            }

            await Task.Delay(ExitPoll, time, ct).ConfigureAwait(false);
        }
    }
}
