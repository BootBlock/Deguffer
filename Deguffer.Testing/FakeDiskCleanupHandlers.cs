using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Testing;

/// <summary>
/// Stands in for Windows' Disk Cleanup handlers, recording every handler and volume handed across.
///
/// <para><b>Injected everywhere, never defaulted.</b> The real <em>Previous Installations</em> handler
/// deletes the previous Windows installation of whoever runs the suite, so every fixture that plans a
/// <see cref="DiskCleanupStep"/> passes one of these.</para>
///
/// <para>Recording is the point, for the reason <see cref="FakeRecycleBinEmptier"/> gives: the handler
/// takes a volume in display form, and no outcome of a cleanup can show which form crossed.</para>
/// </summary>
public sealed class FakeDiskCleanupHandlers : IDiskCleanupHandlers
{
    private readonly Func<string, string, DiskCleanupOutcome> _behaviour;

    private Func<string, CancellationToken, DiskCleanupSurvey> Answer { get; init; } = (_, _) => new DiskCleanupSurvey(DiskCleanupAnswer.HasSomething);

    /// <summary>
    /// Clears, for each handler, the directories its registration names under the volume it is given,
    /// which is what Windows' own effect looks like from here.
    /// </summary>
    /// <param name="reach">Each handler's directories, relative to the volume.</param>
    public FakeDiskCleanupHandlers(IReadOnlyDictionary<string, string[]> reach)
        : this((handler, volume) =>
        {
            foreach (var relative in reach.GetValueOrDefault(handler, []))
            {
                var directory = LongPath.Extended(Path.Combine(volume, relative));

                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }

            return new DiskCleanupOutcome(Ran: true);
        })
    {
    }

    public FakeDiskCleanupHandlers(Func<string, string, DiskCleanupOutcome> behaviour) => _behaviour = behaviour;

    /// <summary>Every call, in order: the handler's name and the volume it was given.</summary>
    public List<(string Handler, string Volume)> Calls { get; } = [];

    /// <summary>
    /// The handlers Windows registers for what an upgrade and a reset leave behind, clearing what each
    /// one's registration names.
    /// </summary>
    public static FakeDiskCleanupHandlers Windows() => new(new Dictionary<string, string[]>
    {
        ["Previous Installations"] = ["Windows.old"],
        ["Temporary Setup Files"] = ["$Windows.~BT"],
        ["Windows ESD installation files"] =
            [Path.Combine("ESD", "Windows"), "$Windows.~WS", Path.Combine("ESD", "Download")],
        ["Upgrade Discarded Files"] = ["$WINDOWS.~Q", "$INPLACE.~TR", "$Windows.~LS"],
        ["Windows Reset Log Files"] =
            [Path.Combine("$SysReset", "Logs"), Path.Combine("$SysReset", "OldOSLogs"), Path.Combine("Windows", "Logs", "PBR")],
    });

    /// <summary>A handler that refuses, as one does with a failing HRESULT.</summary>
    public static FakeDiskCleanupHandlers Refusing(string message) =>
        new((_, _) => new DiskCleanupOutcome(Ran: false, message));

    /// <summary>A handler that reports it finished and removes nothing.</summary>
    public static FakeDiskCleanupHandlers DoingNothing() => new((_, _) => new DiskCleanupOutcome(Ran: true));

    /// <summary>
    /// Handlers that clear what they are registered to and one directory besides — the over-reach
    /// §5.6's negative exists to catch.
    /// </summary>
    public static FakeDiskCleanupHandlers AlsoRemoving(string directory) => new((handler, volume) =>
    {
        var outcome = Windows().Run(handler, volume, CancellationToken.None);
        Directory.Delete(LongPath.Extended(directory), recursive: true);
        return outcome;
    });

    /// <summary>Handlers of which Windows registers none on this machine.</summary>
    public static FakeDiskCleanupHandlers NoneRegistered() => new(Windows()._behaviour)
    {
        Answer = (handler, _) => new DiskCleanupSurvey(DiskCleanupAnswer.Unavailable, $"Windows no longer registers its '{handler}' cleanup."),
    };

    /// <summary>
    /// Handlers that find nothing of their own to clear, whatever the folders hold — what Windows' ESD
    /// cleanup was observed to answer over a folder of setup sources it does not count as its own.
    /// </summary>
    public static FakeDiskCleanupHandlers FindingNothing() => new(Windows()._behaviour)
    {
        Answer = (_, _) => new DiskCleanupSurvey(DiskCleanupAnswer.NothingToClear),
    };

    /// <summary>Handlers this process cannot ask, as Windows' setup handlers are to one that is not elevated.</summary>
    public static FakeDiskCleanupHandlers Unasked() => new(Windows()._behaviour)
    {
        Answer = (_, _) => new DiskCleanupSurvey(DiskCleanupAnswer.NeedsElevation),
    };

    /// <summary>
    /// Handlers whose measurement lasts until <paramref name="measured"/> completes, then answers
    /// <paramref name="answer"/>. Told to stop first, one answers "has something", as the real ones do:
    /// <c>E_ABORT</c> is a failure to measure, and a failure to measure is read as something to clear.
    /// So a plan that read a stopped survey would offer what it should not.
    /// </summary>
    public static FakeDiskCleanupHandlers Measuring(Task measured, DiskCleanupAnswer answer = DiskCleanupAnswer.HasSomething) =>
        new(Windows()._behaviour)
        {
            Answer = (_, ct) =>
            {
                try
                {
                    measured.Wait(ct);
                    return new DiskCleanupSurvey(answer);
                }
                catch (OperationCanceledException)
                {
                    return new DiskCleanupSurvey(DiskCleanupAnswer.HasSomething);
                }
            },
        };

    private readonly Lock _surveys = new();
    private readonly TaskCompletionSource _surveyStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _surveyReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Every handler asked about before a plan offered it.</summary>
    public List<string> Surveyed { get; } = [];

    /// <summary>
    /// Every survey that has returned, and whether it had been told to stop by then. A plan runs its
    /// survey on another thread, so a test reads this to know none was left running.
    /// </summary>
    public List<(string Handler, bool Stopped)> Returned { get; } = [];

    /// <summary>Completes when the first survey starts, so a test can wait for a plan to ask.</summary>
    public Task SurveyStarted => _surveyStarted.Task;

    /// <summary>Completes when the first survey returns, for a caller that does not wait for it.</summary>
    public Task SurveyReturned => _surveyReturned.Task;

    public DiskCleanupSurvey Survey(string handler, string volume, CancellationToken ct)
    {
        lock (_surveys)
        {
            Surveyed.Add(handler);
        }

        _surveyStarted.TrySetResult();
        var answer = Answer(handler, ct);

        lock (_surveys)
        {
            Returned.Add((handler, ct.IsCancellationRequested));
        }

        _surveyReturned.TrySetResult();
        return answer;
    }

    public DiskCleanupOutcome Run(string handler, string volume, CancellationToken ct)
    {
        Calls.Add((handler, volume));
        return _behaviour(handler, volume);
    }
}
