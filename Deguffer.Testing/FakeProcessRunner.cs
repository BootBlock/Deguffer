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

    public List<Invocation> Invocations { get; } = [];

    /// <summary>
    /// Reply per invocation of <paramref name="fileName"/>, returning null to fall through to its
    /// substring responses. Needed where the answer depends on which arguments a particular call
    /// carried rather than on the call being made at all — a command split across several
    /// invocations, for instance.
    /// </summary>
    public FakeProcessRunner Replying(string fileName, Func<string, CommandOutcome?> reply)
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
    /// Keeps <see cref="IProcessRunner.RunAsync"/>'s promise about cancellation, so code under test
    /// meets the same contract the real runner keeps: nothing starts once the run is cancelled, and a
    /// reply given after a cancellation is thrown away for a tool that is
    /// <see cref="CancelledCommand.Ended"/>. A reply can cancel the run, to stand for the user pressing
    /// Cancel while the tool works.
    /// </summary>
    public async Task<CommandOutcome> RunAsync(string fileName, string arguments, ToolRun run, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var outcome = await Task.FromResult(Reply(fileName, arguments, run)).ConfigureAwait(false);

        if (run.WhenCancelled is CancelledCommand.Ended)
        {
            ct.ThrowIfCancellationRequested();
        }

        return outcome;
    }

    private CommandOutcome Reply(string fileName, string arguments, ToolRun run)
    {
        Invocations.Add(new Invocation(fileName, arguments, run));

        if (!_programs.TryGetValue(fileName, out var program))
        {
            throw new InvalidOperationException(
                $"'{fileName}' was run with '{arguments}', and no reply is set for that program. "
                + $"Replies are set for: {(_programs.Count == 0 ? "no program" : string.Join(", ", _programs.Keys.Select(k => $"'{k}'")))}.");
        }

        if (program.Reply?.Invoke(arguments) is { } replied)
        {
            return replied;
        }

        foreach (var (match, outcome) in program.Responses)
        {
            if (arguments.Contains(match, StringComparison.OrdinalIgnoreCase))
            {
                return outcome;
            }
        }

        return new CommandOutcome(0, string.Empty, string.Empty);
    }

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

        public Func<string, CommandOutcome?>? Reply { get; set; }
    }
}

/// <summary>One program the code under test ran, and how it asked for it to be run.</summary>
public sealed record Invocation(string FileName, string Arguments, ToolRun Run);
