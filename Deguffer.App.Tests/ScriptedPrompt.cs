using Deguffer.App.Shell;
using Deguffer.Core.Execution;

namespace Deguffer.App.Tests;

/// <summary>A confirmation dialog that answers every question the same way, and records each one.</summary>
internal sealed class ScriptedPrompt : IConfirmationPrompt
{
    public bool Agrees { get; set; } = true;

    public List<ConfirmationRequirement> Asked { get; } = [];

    public Task<Confirmation?> AskAsync(ConfirmationRequirement requirement, CancellationToken ct = default)
    {
        Asked.Add(requirement);

        return Task.FromResult(Agrees ? new Confirmation(requirement.ProviderId, requirement.RequiredPhrase) : null);
    }
}
