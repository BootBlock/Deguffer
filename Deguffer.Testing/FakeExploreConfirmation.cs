using Deguffer.Core.Exploring.Acting;

namespace Deguffer.Testing;

/// <summary>
/// Answers an Explore removal's question with a fixed yes or no, and records every question it was
/// asked, so a test can see both that the user was asked and what about.
/// </summary>
public sealed class FakeExploreConfirmation(bool answer) : IExploreConfirmationPrompt
{
    public List<ExploreRemovalPrompt> Asked { get; } = [];

    public Task<bool> AskAsync(ExploreRemovalPrompt prompt, CancellationToken ct = default)
    {
        Asked.Add(prompt);

        return Task.FromResult(answer);
    }
}
