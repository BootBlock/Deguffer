using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// Records what a plan would invoke and replies with canned tool output. Nothing is executed,
/// which is the point: a plan's command steps are assertable without npm or the SDK installed.
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Dictionary<string, CommandOutcome> _responses = new(StringComparer.OrdinalIgnoreCase);
    private Func<string, CommandOutcome?>? _reply;

    public List<(string FileName, string Arguments)> Invocations { get; } = [];

    /// <summary>
    /// Reply per invocation, returning null to fall through to the substring responses. Needed
    /// where the answer depends on which arguments a particular call carried rather than on the
    /// call being made at all — a command split across several invocations, for instance.
    /// </summary>
    public FakeProcessRunner Replying(Func<string, CommandOutcome?> reply)
    {
        _reply = reply;
        return this;
    }

    /// <summary>Reply to any invocation whose arguments contain <paramref name="argumentMatch"/>.</summary>
    public FakeProcessRunner Responding(string argumentMatch, string standardOutput, int exitCode = 0)
    {
        _responses[argumentMatch] = new CommandOutcome(exitCode, standardOutput, string.Empty);
        return this;
    }

    public Task<CommandOutcome> RunAsync(string fileName, string arguments, CancellationToken ct)
    {
        Invocations.Add((fileName, arguments));

        if (_reply?.Invoke(arguments) is { } replied)
        {
            return Task.FromResult(replied);
        }

        foreach (var (match, outcome) in _responses)
        {
            if (arguments.Contains(match, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(outcome);
            }
        }

        return Task.FromResult(new CommandOutcome(0, string.Empty, string.Empty));
    }
}
