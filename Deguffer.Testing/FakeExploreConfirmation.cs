using Deguffer.Core.Exploring.Acting;

namespace Deguffer.Testing;

/// <summary>
/// Answers an Explore removal's question with a fixed yes or no, and records every question it was
/// asked, so a test can see both that the user was asked and what about.
///
/// <para>Given a task rather than an answer, it answers when the task does, which holds the removal
/// open for as long as a test needs to change the page under it.</para>
/// </summary>
public sealed class FakeExploreConfirmation(Task<bool> answer) : IExploreConfirmationPrompt
{
    public FakeExploreConfirmation(bool answer)
        : this(Task.FromResult(answer))
    {
    }

    public List<ExploreRemovalPrompt> Asked { get; } = [];

    public Task<bool> AskAsync(ExploreRemovalPrompt prompt, CancellationToken ct = default)
    {
        Asked.Add(prompt);

        return answer.WaitAsync(ct);
    }
}
