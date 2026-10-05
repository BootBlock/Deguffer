using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// What a step's paths hold after it ran, read from the disk, and the first of them the reading could
/// not reach.
///
/// <para>The executor subtracts this from the figure before the run and reports the difference as
/// reclaimed. A path Windows refused afterwards measures zero, so the subtraction would credit the
/// step with everything it was estimated at — a clean reported as complete on no evidence at all. A
/// reading that did not reach every path is not a figure to subtract from, and
/// <see cref="Unreached"/> is how a caller is made to say so. See <see cref="RootReach"/>.</para>
/// </summary>
/// <param name="Size">The total over the paths that were reached.</param>
/// <param name="Unreached">The first path that was not reached, in display form, or null.</param>
internal readonly record struct DiskReading(ScanSize Size, string? Unreached)
{
    /// <summary>The sentence for a reading that did not reach <see cref="Unreached"/>.</summary>
    public string WhyNothingIsCounted =>
        $"Windows would not let Deguffer read '{Unreached}' afterwards, so nothing is counted as reclaimed";
}
