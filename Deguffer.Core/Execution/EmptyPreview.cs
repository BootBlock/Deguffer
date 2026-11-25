namespace Deguffer.Core.Execution;

/// <summary>
/// Why the Storage preview draws none of its rows, which decides what the page says in their place.
/// See <see cref="EmptyPreviewReason.Of"/>.
/// </summary>
public enum EmptyPreview
{
    /// <summary>No scan has produced a row, so the one thing to do is scan.</summary>
    NothingScanned,

    /// <summary>
    /// The scan found locations, and at least one of them is installed and has nothing left to
    /// remove. This is the state a clean that emptied everything leaves, and it is a result, not a
    /// problem to fix.
    /// </summary>
    NothingLeftToClean,

    /// <summary>
    /// As <see cref="NothingLeftToClean"/>, of a scan still running, or one that stopped before it
    /// finished. The locations it has not reached may hold anything, so it claims only what it has
    /// seen.
    /// </summary>
    NothingToCleanSoFar,

    /// <summary>
    /// The scan found locations, and every one belongs to a tool this machine does not have, which
    /// the not-installed filter hides.
    /// </summary>
    HiddenByFilters,
}

/// <summary>How much of the scan the rows describe.</summary>
public enum PreviewExtent
{
    /// <summary>A scan still running, or one cancelled or failed part-way.</summary>
    Partial,

    /// <summary>A scan that reached every location it set out to.</summary>
    Whole,
}

/// <summary>The rule that names an <see cref="EmptyPreview"/>.</summary>
public static class EmptyPreviewReason
{
    /// <summary>
    /// Why a preview that draws none of <paramref name="found"/> draws nothing.
    ///
    /// <para>For a preview that draws none of its rows, and never asked otherwise. A row that is not
    /// drawn is hidden by <see cref="PreviewFilter"/>, which hides only a row that is already clear or
    /// not installed, so neither can hold anything to remove. One clear location is enough to say
    /// there is nothing left to clean. Only a scan whose every location is not installed is the
    /// filter's doing alone, because "nothing left" would describe a machine that never had
    /// anything.</para>
    ///
    /// <para>"Nothing left" is a verdict on the whole scan, so a partial one says "so far": the
    /// providers that finish first are often the ones with nothing to report.</para>
    /// </summary>
    /// <param name="found">The status of every row the scan produced, drawn or not.</param>
    /// <param name="extent">Whether those rows are the whole scan.</param>
    public static EmptyPreview Of(IEnumerable<FindingStatus> found, PreviewExtent extent)
    {
        var any = false;

        foreach (var status in found)
        {
            if (status == FindingStatus.AlreadyClear)
            {
                return extent == PreviewExtent.Whole ? EmptyPreview.NothingLeftToClean : EmptyPreview.NothingToCleanSoFar;
            }

            any = true;
        }

        return any ? EmptyPreview.HiddenByFilters : EmptyPreview.NothingScanned;
    }
}
