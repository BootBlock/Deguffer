using Deguffer.Core.Duplicates;

namespace Deguffer.Testing;

/// <summary>
/// Answers a duplicate removal's question with a fixed yes or no, and records every confirmation it
/// was shown, so a test can see both that the user was asked and what they were told.
///
/// <para>Given a task rather than an answer, it answers when the task does, which holds the removal
/// open for as long as a test needs to try the page under it.</para>
/// </summary>
public sealed class FakeDuplicateConfirmation(Task<bool> answer) : IDuplicateConfirmationPrompt
{
    public FakeDuplicateConfirmation(bool answer)
        : this(Task.FromResult(answer))
    {
    }

    public List<RemovalConfirmation> Asked { get; } = [];

    public Task<bool> AskAsync(RemovalConfirmation confirmation, CancellationToken ct = default)
    {
        Asked.Add(confirmation);

        return answer.WaitAsync(ct);
    }
}
