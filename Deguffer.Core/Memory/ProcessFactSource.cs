namespace Deguffer.Core.Memory;

/// <inheritdoc />
/// <remarks>
/// <para><b>One open, for one process, for one row.</b> The handle is opened when the caller asks and
/// closed before the answer is returned, and every fact is read through it, so the identifier cannot
/// pass to another process in the middle of the reading (§7.2.1). Nothing here reads anything about
/// any other process.</para>
///
/// <para><b>Identity is decided first, and nothing is read once it fails.</b> A process whose creation
/// time is not the one the caller recorded is not the process the caller picked, and facts about the
/// stranger now holding the identifier would be worse than no facts at all.</para>
///
/// <para>One instance for the process (G5). What Deguffer's own process is cannot change while it
/// runs, so it is read once and kept — but only once it has been read whole, because a partial reading
/// would otherwise refuse every process for the life of the run.</para>
/// </remarks>
public sealed class ProcessFactSource : IProcessFactSource
{
    public static readonly ProcessFactSource Default = new(ProcessCalls.Instance, WindowCalls.Instance);

    private readonly Lock _gate = new();
    private readonly IProcessCalls _processes;
    private readonly IWindowCalls _windows;
    private OwnProcess? _own;

    internal ProcessFactSource(IProcessCalls processes, IWindowCalls windows)
    {
        _processes = processes;
        _windows = windows;
    }

    public ProcessFacts Read(int processId, long creationTime, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ct.ThrowIfCancellationRequested();

        var opening = _processes.Open(processId);

        if (opening.Process is not { } process)
        {
            return ProcessFacts.NothingRead(
                opening.Outcome is OpenOutcome.NotRunning ? Answer.No : Answer.Unreadable);
        }

        using (process)
        {
            return Through(process, processId, creationTime, ct);
        }
    }

    /// <summary>
    /// The same facts, read through a handle the caller already holds and goes on holding.
    ///
    /// <para><b>For §7.2.1's second decision.</b> <c>ProcessCloser</c> opens the process, decides
    /// every refusal again and posts, all under one handle, so the identifier cannot pass to another
    /// process anywhere in the middle of that. Reading the facts through a second open of its own
    /// would work — nothing can take an identifier Deguffer holds — but it would be a second open
    /// that can fail on its own, and the decision it feeds is the last one before a message
    /// leaves.</para>
    /// </summary>
    internal ProcessFacts Through(IOpenProcess process, int processId, long creationTime, CancellationToken ct)
    {
        var own = Own();

        if (Present(process, creationTime) is var present && present is not Answer.Yes)
        {
            return ProcessFacts.NothingRead(present);
        }

        ct.ThrowIfCancellationRequested();

        var token = process.Token();
        var survey = WindowSurveyor.Take(_windows, processId);

        return new ProcessFacts(
            Answer.Yes,
            Same(process.SessionId(), own.SessionId),
            Same(token.User, own.User),
            IntegrityLevel.Above(token.IntegrityLevel, own.IntegrityLevel),
            Of(process.IsCritical()),
            Package(process),
            survey.OwnsConsoleWindow,
            survey.Qualifying);
    }

    /// <summary>
    /// Whether the open process is the one the caller picked. A holder created <em>later</em> than the
    /// recorded time is a successor, so the picked process has gone. A holder created
    /// <em>earlier</em> says the recorded time is not this identifier's, because no successor predates
    /// the process it replaced, and nothing follows from that — which is
    /// <see cref="Safety.ProcessLiveness.StateOfProcessStartedAt"/>'s reasoning for the same question.
    /// </summary>
    private static Answer Present(IOpenProcess process, long creationTime)
    {
        if (process.CreationTime() is not { } created)
        {
            return Answer.Unreadable;
        }

        if (created != creationTime)
        {
            return created > creationTime ? Answer.No : Answer.Unreadable;
        }

        // An exited process still held open keeps its identifier and its creation time, so the wait is
        // what says it has gone.
        return Of(process.HasExited()) switch
        {
            Answer.Yes => Answer.No,
            Answer.No => Answer.Yes,
            _ => Answer.Unreadable,
        };
    }

    private static PackageAnswer Package(IOpenProcess process) => process.Package() switch
    {
        PackageIdentity.NotPackaged => PackageAnswer.NotPackaged,
        PackageIdentity.Packaged => process.IsFrozen() switch
        {
            true => PackageAnswer.Suspended,
            false => PackageAnswer.Running,
            null => PackageAnswer.StateUnreadable,
        },
        _ => PackageAnswer.Unreadable,
    };

    private static Answer Of(bool? read) => read switch
    {
        true => Answer.Yes,
        false => Answer.No,
        null => Answer.Unreadable,
    };

    private static Answer Same(uint? target, uint? own) =>
        target is not { } theirs || own is not { } ours ? Answer.Unreadable : Of(theirs == ours);

    private static Answer Same(string? target, string? own) =>
        target is null || own is null ? Answer.Unreadable : Of(string.Equals(target, own, StringComparison.Ordinal));

    private OwnProcess Own()
    {
        lock (_gate)
        {
            if (_own is { } kept)
            {
                return kept;
            }

            var own = _processes.Own();

            if (own.Complete)
            {
                _own = own;
            }

            return own;
        }
    }
}
