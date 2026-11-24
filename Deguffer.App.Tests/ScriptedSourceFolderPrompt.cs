using Deguffer.App.Shell;
using Deguffer.Core.Configuration;

namespace Deguffer.App.Tests;

/// <summary>
/// The picker and the warning, answered as a test scripts them: the folder the user picks, or none,
/// and whether they accept a warning. Records each warning it was asked about.
/// </summary>
internal sealed class ScriptedSourceFolderPrompt : ISourceFolderPrompt
{
    /// <summary>What the picker returns, or null for a picker the user closed.</summary>
    public string? Picked { get; set; }

    /// <summary>Whether the user accepts a warning about the folder.</summary>
    public bool Accepts { get; set; } = true;

    /// <summary>Every approval the user was asked to confirm, in order.</summary>
    public List<SourceRootApproval> Asked { get; } = [];

    /// <summary>Run as the picker opens, so a test can see what the page allows while it is up.</summary>
    public Action? WhilePicking { get; set; }

    public Task<string?> PickAsync()
    {
        WhilePicking?.Invoke();
        return Task.FromResult(Picked);
    }

    public Task<bool> AcceptsAsync(SourceRootApproval approval, CancellationToken ct)
    {
        Asked.Add(approval);
        return Task.FromResult(Accepts);
    }
}
