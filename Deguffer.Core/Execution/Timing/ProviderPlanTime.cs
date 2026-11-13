using Deguffer.Core.Providers;

namespace Deguffer.Core.Execution.Timing;

/// <summary>
/// When one provider's plan ran within a planning pass (#443): from the moment it took one of the
/// pass's slots to the moment its plan ended, each measured from the start of the pass.
///
/// <para>Offsets rather than a duration alone, because what a pass waits on is when each row ran as
/// much as how long: a slow row that starts late ends the pass, and the same row started first does
/// not.</para>
/// </summary>
/// <param name="Provider">The provider that planned.</param>
/// <param name="Began">When it took a slot, from the start of the pass.</param>
/// <param name="Ended">When its plan ended, from the start of the pass.</param>
public readonly record struct ProviderPlanTime(ICleanupProvider Provider, TimeSpan Began, TimeSpan Ended)
{
    /// <summary>How long its plan took, its presence check included.</summary>
    public TimeSpan Elapsed => Ended - Began;
}
