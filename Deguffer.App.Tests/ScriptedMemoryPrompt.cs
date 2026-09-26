using Deguffer.App.Shell;
using Deguffer.Core.Memory.Acting;

namespace Deguffer.App.Tests;

/// <summary>The user at the confirmation: a fixed answer, and a count of every time they were asked.</summary>
internal sealed class ScriptedMemoryPrompt(bool answer) : IMemoryConfirmationPrompt
{
    public int Asked { get; private set; }

    public MemoryClosePrompt? Last { get; private set; }

    public Task<bool> AskAsync(MemoryClosePrompt prompt, CancellationToken ct = default)
    {
        Asked++;
        Last = prompt;

        return Task.FromResult(answer);
    }
}
