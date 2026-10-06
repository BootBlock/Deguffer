using Deguffer.Core.Execution;

namespace Deguffer.Testing;

/// <summary>
/// A Windows session that records what it was asked to do and does none of it, so a test can follow
/// a clean all the way to a shutdown without the machine running the tests going anywhere.
/// </summary>
public sealed class FakeWindowsSession : IWindowsSession
{
    public bool CanSleep { get; set; } = true;

    public bool CanHibernate { get; set; } = true;

    /// <summary>What Windows says when it refuses, or null for a session that accepts everything.</summary>
    public string? Refusal { get; set; }

    public List<CompletionAction> Performed { get; } = [];

    /// <summary>Refuses what the real session refuses, so a caller that hands it the window's action fails here too.</summary>
    public string? Perform(CompletionAction action)
    {
        if (action is CompletionAction.Nothing or CompletionAction.ExitDeguffer)
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, "Not something the Windows session does.");
        }

        Performed.Add(action);
        return Refusal;
    }
}
