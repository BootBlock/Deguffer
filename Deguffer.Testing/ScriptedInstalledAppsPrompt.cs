using Deguffer.Core.InstalledApps;

namespace Deguffer.Testing;

/// <summary>A confirmation that answers as the test says and records what it was asked.</summary>
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
        Prompts.Add(prompt);
        WhileAsking?.Invoke(prompt);
        return Task.FromResult(Answer);
    }
}
