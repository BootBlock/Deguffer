namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// What the first pass over a table produced.
/// </summary>
/// <param name="WholeTable">False where a region could not be read, or the table wants more extension records than it holds.</param>
/// <param name="Abandoned">Whether the handler asked to stop.</param>
/// <param name="Deferred">The records whose attributes continue in extension records, in record order.</param>
/// <param name="Wanted">How many extension records <paramref name="Deferred"/> wants between them.</param>
internal readonly record struct MftFirstPass(
    bool WholeTable, bool Abandoned, IReadOnlyList<MftDeferredRecord> Deferred, long Wanted);
