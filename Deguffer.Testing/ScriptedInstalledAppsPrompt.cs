using Deguffer.Core.InstalledApps;

namespace Deguffer.Testing;

/// <summary>
/// A confirmation that answers as the test says and records what it was asked. It treats its token
/// as the dialog does: a token cancelled before it asks throws, and one cancelled while it asks closes
/// it unanswered.
/// </summary>
public sealed class ScriptedInstalledAppsPrompt(bool answer) : IInstalledAppsConfirmation
{
    public bool Answer { get; set; } = answer;

    /// <summary>Runs as each question is asked, before it is answered.</summary>
    public Action<InstalledAppsPrompt>? WhileAsking { get; set; }

    /// <summary>Every question asked, in order.</summary>
    public List<InstalledAppsPrompt> Prompts { get; } = [];

    public int Asked => Prompts.Count;

    public Task<bool> AskAsync(InstalledAppsPrompt prompt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Prompts.Add(prompt);
        WhileAsking?.Invoke(prompt);
        return Task.FromResult(Answer && !ct.IsCancellationRequested);
    }
}
