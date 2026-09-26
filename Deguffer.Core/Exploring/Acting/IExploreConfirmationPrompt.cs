namespace Deguffer.Core.Exploring.Acting;

/// <summary>
/// Puts an Explore removal to the user. What is asked and what they are told comes from
/// <see cref="ExploreRemovalPrompt"/>; this seam only carries it to a surface that can ask.
///
/// Separate from the Storage page's confirmation prompt rather than a second method on it, because
/// the two ask different questions of different things. That one renders a §7 requirement about a
/// named provider's plan and may demand a typed phrase; this one asks about a file the user picked
/// out of a picture, which no provider has classified and no tier applies to.
/// </summary>
public interface IExploreConfirmationPrompt
{
    /// <summary>Whether the user said yes. Declining is a decision, not a failure.</summary>
    Task<bool> AskAsync(ExploreRemovalPrompt prompt, CancellationToken ct = default);
}
