namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// What the first pass over a table produced.
/// </summary>
/// <param name="WholeTable">False where a region could not be read, or the table wants more extension records than it holds.</param>
/// <param name="Abandoned">Whether the handler asked to stop.</param>
/// <param name="Deferred">The records whose attributes continue in extension records, in record order.</param>
/// <param name="Wanted">How many extension records <paramref name="Deferred"/> wants between them.</param>
/// <param name="Extensions">
/// What every extension record the pass read holds, where it is in use, readable and holds something
/// the scan needs, in record order.
/// </param>
/// <param name="Reached">
/// How far the pass dealt with the table: every record before this one was read, or known from the
/// bitmap to be free, and none from it on was. The table's count where it was read whole.
/// </param>
internal readonly record struct MftFirstPass(
    bool WholeTable,
    bool Abandoned,
    IReadOnlyList<MftDeferredRecord> Deferred,
    long Wanted,
    IReadOnlyList<MftExtensionRecord> Extensions,
    long Reached);
