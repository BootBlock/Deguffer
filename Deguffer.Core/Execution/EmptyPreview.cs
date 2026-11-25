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
    /// The scan found locations, and every one belongs to a tool this machine does not have, which
    /// the not-installed filter hides.
    /// </summary>
    HiddenByFilters,
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
    /// </summary>
    /// <param name="found">The status of every row the scan produced, drawn or not.</param>
    public static EmptyPreview Of(IEnumerable<FindingStatus> found)
    {
        var any = false;

        foreach (var status in found)
        {
            if (status == FindingStatus.AlreadyClear)
            {
                return EmptyPreview.NothingLeftToClean;
            }

            any = true;
        }

        return any ? EmptyPreview.HiddenByFilters : EmptyPreview.NothingScanned;
    }
}
