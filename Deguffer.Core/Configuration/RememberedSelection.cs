namespace Deguffer.Core.Configuration;

/// <summary>
/// What one provider's row on the Storage page was left ticked as, so the next scan can start
/// where the user left off rather than back at the tier defaults.
///
/// Both levels are recorded because the row is only a roll-up of its steps. Remembering the row
/// alone would restore a ticked row by ticking every step in it, which silently re-selects the
/// per-workspace folders the user had picked out and left alone — and that is the one direction a
/// mistake here must never go.
/// </summary>
/// <param name="IsSelected">Whether the row itself was ticked.</param>
/// <param name="Steps">
/// Each step of that row, keyed by <see cref="Execution.CleanupStep.SelectionKey"/>. Holds the
/// steps of the scan this was written from, so it shrinks and grows with the machine rather than
/// accumulating every path Deguffer has ever planned. A step the map does not mention is one this
/// entry knows nothing about, and it starts at whatever the row says.
/// </param>
public sealed record RememberedSelection(bool IsSelected, IReadOnlyDictionary<string, bool> Steps);
