using System.ComponentModel;

using Deguffer.Core.Execution;

namespace Deguffer.Core.Memory.Acting;

/// <summary>
/// Carries out §7.2.1's one action: asking one program to close itself, and watching what it does.
///
/// <para><b>Everything happens under one handle.</b> Deguffer opens the process with
/// <c>PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE</c>, reads its creation time back through it,
/// decides every refusal again from facts read through it, posts, and watches it, all before the
/// handle is closed. A process identifier is valid until every handle to the process is closed, so
/// while Deguffer holds one, nothing else can take the number it is about to post to. That removes
/// the reuse race rather than narrowing it.</para>
///
/// <para><b>Every window is checked again immediately before its own message.</b> A window handle is
/// recycled as an identifier is, so a window that has passed to another process receives nothing.</para>
///
/// <para><b>One pass, no retry, no escalation.</b> Nothing is sent twice, no attention is paid to a
/// window the program opens afterwards, and a program still running when the watch ends is reported
/// as still running. There is nothing stronger to offer, and no preference adds one.</para>
///
/// <para><b>The watch has no deadline.</b> A save prompt waits for a person, so a timer would report
/// "still running" about a program doing exactly what it was asked. It ends when the process exits,
/// or when the caller stops watching, which is the user dismissing the result or leaving the page.</para>
/// </summary>
public sealed class ProcessCloser
{
    /// <summary>
    /// How often the watch asks the handle it holds whether the process has gone.
    ///
    /// <para>A question rather than a blocking wait, because the watch has to end on the user's
    /// cancellation as well, and a wait registered on a handle the action is about to close is a race
    /// worth not having. A quarter of a second is imperceptible against a person answering a save
    /// prompt, and each question is one kernel call about one handle.</para>
    /// </summary>
    public static readonly TimeSpan WatchCadence = TimeSpan.FromMilliseconds(250);

    private readonly IProcessCalls _processes;
    private readonly IWindowCalls _windows;
    private readonly IMemorySource _memory;
    private readonly TimeProvider _time;
    private readonly ProcessFactSource _facts;
    private readonly IDesktopFacts _desktop;
    private readonly MemoryActionPolicy _policy;

    internal ProcessCloser(
        IProcessCalls processes,
        IWindowCalls windows,
        IDesktopFacts desktop,
        IMemorySource memory,
        TimeProvider time,
        int? ownProcessId = null)
    {
        _processes = processes;
        _windows = windows;
        _desktop = desktop;
        _memory = memory;
        _time = time;
        _facts = new ProcessFactSource(processes, windows);
        _policy = new MemoryActionPolicy(_facts, _desktop, ownProcessId);
    }

    /// <param name="memory">Where the reads before and after the action come from, as the page's own do.</param>
    public static ProcessCloser For(IMemorySource memory, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(memory);

        return new ProcessCloser(
            ProcessCalls.Instance,
            WindowCalls.Instance,
            DesktopFacts.Default,
            memory,
            time ?? TimeProvider.System);
    }

    /// <summary>
    /// Ask <paramref name="target"/> to close, and watch until it exits or the caller stops watching.
    /// </summary>
    /// <param name="target">
    /// The process the user picked, as the snapshot they picked it from describes it. It is identified
    /// by identifier and creation time, and a machine that has moved on refuses rather than acts.
    /// </param>
    /// <param name="watching">
    /// Given the report the moment the last message is posted, so a page can say what was asked while
    /// the program is still deciding. What the watch came to is what this returns.
    /// </param>
    /// <param name="ct">
    /// Ends the watch, and nothing else: the messages are already posted, and §5.6 still runs. A close
    /// cannot be called off.
    /// </param>
    public async Task<CloseAttempt> CloseAsync(
        ProcessMemory target,
        IProgress<CloseReport>? watching = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        // The read the action is judged against: the commit charge before, the processes that must
        // survive, and the target's descendants. Taken here rather than reused from the page, because
        // the page's own read is up to two seconds old and this one is about to post.
        var read = await Task.Run(() => _memory.Read(ct), ct).ConfigureAwait(false);
        var before = ProcessTree.Of(read);

        // Nothing below can be decided or asserted from a read that cannot tell one process from
        // another. Section 7.2.1's refusal of Deguffer's own tree is a walk of these records, and
        // its Section 5.6 assertions compare them against a later read. Refusing costs the user a
        // second attempt; acting would cost an action nobody could account for.
        if (!before.Identifies)
        {
            return new CloseAttempt(
                MemoryVerdict.Refuse(
                    "Deguffer could not read this machine's processes well enough to tell them apart "
                    + "just now, so it cannot say what would go with this program, or whether it is "
                    + "one of Deguffer's own. Nothing was sent."),
                Report: null);
        }

        if (target.CreationTime is not { } created)
        {
            return new CloseAttempt(_policy.For(read, target, ct), Report: null);
        }

        var opening = _processes.Open(target.ProcessId);
        var shellOwner = _desktop.ShellWindowOwner();

        if (opening.Process is not { } open)
        {
            return new CloseAttempt(
                _policy.Decide(read, target, ProcessFacts.NothingRead(Missing(opening.Outcome)), shellOwner),
                Report: null);
        }

        using (open)
        {
            var verdict = _policy.Decide(
                read, target, _facts.Through(open, target.ProcessId, created, ct), shellOwner);

            if (!verdict.IsAllowed)
            {
                return new CloseAttempt(verdict, Report: null);
            }

            // Posted the moment the verdict allows it, with nothing in between. Whatever else this
            // action has to do, none of it belongs between deciding and sending.
            var (sent, moved) = Post(verdict.Windows, target);

            if (sent.Count == 0)
            {
                // Every window changed hands between the survey and the post. Nothing was sent, so
                // there is nothing to watch and nothing to report.
                return new CloseAttempt(
                    MemoryVerdict.Refuse(
                        "The windows Deguffer was about to ask now belong to another program, so it "
                        + "sent nothing at all. The picture will show what is there when it next reads."),
                    Report: null);
            }

            // The desktop is named from the read taken before any of this, so finding it afterwards
            // changes nothing about what is asserted, and it keeps these opens out of the moment
            // between the decision and the message.
            //
            // Not cancellable, for the reason the read below is not: the messages have gone, so
            // everything from here on is evidence the user is owed, and a cancellation that threw it
            // away would leave a close nobody can account for.
            var desktop = DesktopProcesses.Of(before, shellOwner, _processes, CancellationToken.None);

            watching?.Report(CloseReport.Watching(target, sent.Count, read.System, moved));

            var exited = await WatchAsync(open, ct).ConfigureAwait(false);
            var after = await ReadAfterAsync().ConfigureAwait(false);

            var verification = new VerificationResult
            {
                Checks =
                [
                    CloseEvidence.Opened(target),
                    .. sent,
                    .. CloseEvidence.Of(before, after is null ? null : ProcessTree.Of(after), target, desktop),
                ],
            };

            return new CloseAttempt(
                verdict,
                exited
                    ? CloseReport.Closed(target, sent.Count, read.System, after?.System, verification, moved)
                    : CloseReport.StillRunning(target, sent.Count, read.System, verification, moved));
        }
    }

    private static Answer Missing(OpenOutcome outcome) =>
        outcome is OpenOutcome.NotRunning ? Answer.No : Answer.Unreadable;

    /// <summary>
    /// The machine as the watch ended, or null where Windows would not describe it.
    ///
    /// <para>Not cancellable, and deliberately: this read is what §5.6 is established from, and the
    /// watch ending is exactly when the user is owed the evidence.</para>
    ///
    /// <para><b>A read that fails is the one place a close cannot give up.</b> The messages are
    /// posted and cannot be recalled, so the record of what was sent has to reach the user whatever
    /// the machine says next. The failure is carried as assertions nobody could make rather than as
    /// an exception that takes the whole report with it.</para>
    /// </summary>
    private async Task<MemorySnapshot?> ReadAfterAsync()
    {
        try
        {
            return await Task.Run(() => _memory.Read(CancellationToken.None)).ConfigureAwait(false);
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Post to every window that still belongs to the target, and count the ones that no longer do.
    ///
    /// <para>In the order Windows enumerated them, one pass. The owner is asked again immediately
    /// before each message rather than once for the whole set, because the set was read a moment ago
    /// and a window handle is recycled.</para>
    /// </summary>
    private (IReadOnlyList<VerificationCheck> Sent, int Moved) Post(
        IReadOnlyList<ProcessWindow> windows, ProcessMemory target)
    {
        var sent = new List<VerificationCheck>(windows.Count);
        var moved = 0;

        foreach (var window in windows)
        {
            if (_windows.ProcessOf(window.Handle) != target.ProcessId)
            {
                moved++;
                continue;
            }

            sent.Add(CloseEvidence.Posted(window, target, _windows.PostClose(window.Handle)));
        }

        return (sent, moved);
    }

    /// <summary>
    /// Watch the handle until the process exits or the caller stops watching, and say which happened.
    ///
    /// <para>Exit is asked by waiting on the handle rather than by an exit code, because a process may
    /// exit with <c>STILL_ACTIVE</c>'s own value. A wait that will not answer reads as still running,
    /// which is the direction that claims nothing.</para>
    /// </summary>
    private async Task<bool> WatchAsync(IOpenProcess open, CancellationToken ct)
    {
        while (open.HasExited() is not true)
        {
            try
            {
                await Task.Delay(WatchCadence, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The user dismissed the result or left the page. Whether the program had gone by
                // then is still worth asking once, so the report is about the machine rather than
                // about the moment the watch was called off.
                return open.HasExited() is true;
            }
        }

        return true;
    }
}
