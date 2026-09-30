using Deguffer.Core.InstalledApps;

namespace Deguffer.App.Tests;

/// <summary>A confirmation that answers as the test says and counts what it was asked.</summary>
internal sealed class ScriptedInstalledAppsPrompt(bool answer) : IInstalledAppsConfirmation
{
    public bool Answer { get; set; } = answer;

    public int Asked { get; private set; }

    public Task<bool> AskAsync(InstalledAppsPrompt prompt, CancellationToken ct)
    {
        Asked++;
        return Task.FromResult(Answer);
    }
}
