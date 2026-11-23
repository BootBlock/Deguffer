namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// When a pass over the file table reads the <c>$ATTRIBUTE_LIST</c>s kept outside the table. The
/// records each pass hands on, and what a table that wants more extension records than it holds
/// loses, are the same either way: see <see cref="MftListReadAhead"/>.
/// </summary>
public enum AttributeListReads
{
    /// <summary>
    /// Once the first pass has ended, all together. Every list waits behind the last read of the
    /// table, and the disk then serves them alone.
    /// </summary>
    AfterFirstPass,

    /// <summary>
    /// While the first pass reads, in batches as the records naming them are parsed, so the small
    /// scattered reads of the lists share the disk with the large reads of the table rather than
    /// waiting behind them.
    /// </summary>
    BesideFirstPass,
}
