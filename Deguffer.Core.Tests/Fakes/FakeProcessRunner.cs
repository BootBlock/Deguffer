using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// Records what a plan would invoke and replies with canned tool output. Nothing is executed,
/// which is the point: a plan's command steps are assertable without npm or the SDK installed.
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Dictionary<string, CommandOutcome> _responses = new(StringComparer.OrdinalIgnoreCase);

    public List<(string FileName, string Arguments)> Invocations { get; } = [];

    /// <summary>Reply to any invocation whose arguments contain <paramref name="argumentMatch"/>.</summary>
    public FakeProcessRunner Responding(string argumentMatch, string standardOutput, int exitCode = 0)
    {
        _responses[argumentMatch] = new CommandOutcome(exitCode, standardOutput, string.Empty);
        return this;
    }

    public Task<CommandOutcome> RunAsync(string fileName, string arguments, CancellationToken ct)
    {
        Invocations.Add((fileName, arguments));

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
