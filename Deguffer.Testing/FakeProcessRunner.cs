using Deguffer.Core.Safety;

namespace Deguffer.Testing;

/// <summary>
/// Records what a plan would invoke and replies with canned tool output. Nothing is executed,
/// which is the point: a plan's command steps are assertable without npm or the SDK installed.
///
/// <para>Every reply is for one program, named by the full path the code under test must run, and
/// a call to any program no reply names throws. Which program runs is part of what a command step
/// means: matched on arguments alone, a step that named the wrong program, or one that was never
/// resolved, got the same reply as the right one and the test stayed green.</para>
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Dictionary<string, Program> _programs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Invocation> _invocations = [];
    private readonly Lock _recording = new();

    /// <summary>
    /// Every run so far in the order each started, as a copy, because code under test may start its
    /// runs from several threads at once.
    /// </summary>
    public IReadOnlyList<Invocation> Invocations
    {
        get
        {
            lock (_recording)
            {
                return [.. _invocations];
            }
        }
    }

    /// <summary>
    /// Reply per invocation of <paramref name="fileName"/>, returning null to fall through to its
    /// substring responses. Needed where the answer depends on which arguments a particular call
    /// carried rather than on the call being made at all — a command split across several
    /// invocations, for instance.
    /// </summary>
    public FakeProcessRunner Replying(string fileName, Func<string, CommandOutcome?> reply) =>
        Replying(fileName, (arguments, _) => reply(arguments));

    /// <summary>
    /// Reply per invocation as <see cref="Replying(string, Func{string, CommandOutcome?})"/> does, seeing
    /// how the run was asked for too, so a program can answer from the environment it was started with
    /// (<see cref="ToolRun.Environment"/>).
    /// </summary>
    public FakeProcessRunner Replying(string fileName, Func<string, ToolRun, CommandOutcome?> reply)
    {
        For(fileName).Reply = reply;
        return this;
    }

    /// <summary>
    /// Reply to any invocation of <paramref name="fileName"/> whose arguments contain
    /// <paramref name="argumentMatch"/>. Any other invocation of it succeeds with no output.
    /// </summary>
    public FakeProcessRunner Responding(string fileName, string argumentMatch, string standardOutput, int exitCode = 0)
    {
        For(fileName).Responses[argumentMatch] = new CommandOutcome(exitCode, standardOutput, string.Empty);
        return this;
    }

    /// <summary>
    /// Keep each run of <paramref name="fileName"/> whose arguments contain
    /// <paramref name="argumentMatch"/> open until <paramref name="held"/> releases it, then reply as
    /// the program's other replies say. A run that matches no hold replies at once.
    /// </summary>
    public FakeProcessRunner Holding(string fileName, string argumentMatch, HeldRuns held)
    {
        For(fileName).Holds.Add((argumentMatch, held));
        return this;
    }

    /// <summary>
    /// Keeps <see cref="IProcessRunner.RunAsync"/>'s promise about cancellation, so code under test
    /// meets the same contract the real runner keeps: nothing starts once the run is cancelled, and a
    /// reply given after a cancellation is thrown away for a tool that is
    /// <see cref="CancelledCommand.Ended"/>. A reply can cancel the run, to stand for the user pressing
    /// Cancel while the tool works.
    /// </summary>
    public async Task<CommandOutcome> RunAsync(string fileName, string arguments, ToolRun run, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_recording)
        {
            _invocations.Add(new Invocation(fileName, arguments, run));
        }

        var program = ProgramFor(fileName, arguments);
        var held = program.HoldFor(arguments);
        CommandOutcome outcome;

        if (held is null)
        {
            outcome = program.ReplyTo(arguments, run);
        }
        else
        {
            await held.EnterAsync().ConfigureAwait(false);

            try
            {
                outcome = program.ReplyTo(arguments, run);
            }
            finally
            {
                held.Leave();
            }
        }

        if (run.WhenCancelled is CancelledCommand.Ended)
        {
            ct.ThrowIfCancellationRequested();
        }

        return outcome;
    }

    private Program ProgramFor(string fileName, string arguments) =>
        _programs.TryGetValue(fileName, out var program)
            ? program
            : throw new InvalidOperationException(
                $"'{fileName}' was run with '{arguments}', and no reply is set for that program. "
                + $"Replies are set for: {(_programs.Count == 0 ? "no program" : string.Join(", ", _programs.Keys.Select(k => $"'{k}'")))}.");

    private Program For(string fileName)
    {
        if (!_programs.TryGetValue(fileName, out var program))
        {
            _programs[fileName] = program = new Program();
        }

        return program;
    }

    private sealed class Program
    {
        public Dictionary<string, CommandOutcome> Responses { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Func<string, ToolRun, CommandOutcome?>? Reply { get; set; }

        public List<(string ArgumentMatch, HeldRuns Held)> Holds { get; } = [];

        public HeldRuns? HoldFor(string arguments) =>
            Holds.FirstOrDefault(hold => arguments.Contains(hold.ArgumentMatch, StringComparison.OrdinalIgnoreCase)).Held;

        public CommandOutcome ReplyTo(string arguments, ToolRun run)
        {
            if (Reply?.Invoke(arguments, run) is { } replied)
            {
                return replied;
            }

            foreach (var (match, outcome) in Responses)
            {
                if (arguments.Contains(match, StringComparison.OrdinalIgnoreCase))
                {
                    return outcome;
                }
            }

            return new CommandOutcome(0, string.Empty, string.Empty);
        }
    }
}

/// <summary>One program the code under test ran, and how it asked for it to be run.</summary>
public sealed record Invocation(string FileName, string Arguments, ToolRun Run);
