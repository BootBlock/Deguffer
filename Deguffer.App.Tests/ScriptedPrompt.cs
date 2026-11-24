using Deguffer.App.Shell;
using Deguffer.Core.Execution;

namespace Deguffer.App.Tests;

/// <summary>
/// A clean's confirmation that answers the same way every time, and records each one it is shown. Agreeing
/// ticks every acknowledgement and types every phrase, as a user who read each row would.
/// </summary>
internal sealed class ScriptedPrompt : IConfirmationPrompt
{
    public bool Agrees { get; set; } = true;

    /// <summary>The rows the user unticks before agreeing, by provider id.</summary>
    public HashSet<string> Unticks { get; } = [];

    public List<CleanConfirmation> Asked { get; } = [];

    /// <summary>Run as each question is asked, so a test can look at the page while the dialog would be open.</summary>
    public Action? WhileAsking { get; set; }

    public Task<IReadOnlyList<CleanConfirmationReply>?> AskAsync(CleanConfirmation confirmation, CancellationToken ct = default)
    {
        Asked.Add(confirmation);
        WhileAsking?.Invoke();

        IReadOnlyList<CleanConfirmationReply>? replies = Agrees
            ?
            [
                .. confirmation.Items.Select(item => new CleanConfirmationReply(
                    item.ProviderId,
                    Included: !Unticks.Contains(item.ProviderId),
                    Acknowledged: true,
                    item.Typing?.Phrase)),
            ]
            : null;

        return Task.FromResult(replies);
    }
}
