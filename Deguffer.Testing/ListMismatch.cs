namespace Deguffer.Testing;

/// <summary>
/// How a file's <c>$ATTRIBUTE_LIST</c> can fail to describe the extension record it names. On a
/// live volume each is a file that changed between the read of its base record and the read of
/// that one, or damage.
/// </summary>
public enum ListMismatch
{
    /// <summary>The extension record was freed and reused since the list was written.</summary>
    ItsOwnSequence,

    /// <summary>The extension record now belongs to another file.</summary>
    OwnerNumber,

    /// <summary>The extension record names the owner's record as it was before that record was reused.</summary>
    OwnerSequence,

    /// <summary>The extension record is the file's own, and no longer holds what the list says it does.</summary>
    HoldsNothingListed,

    /// <summary>The list names a record past the end of the table, which no read can reach.</summary>
    OutsideTheTable,

    /// <summary>
    /// The list names the base record itself as it was before that record was reused, so it is a
    /// list from another moment and nothing it says about where attributes went can be trusted.
    /// </summary>
    ListNamesItsOwnRecordAsItWas,
}
