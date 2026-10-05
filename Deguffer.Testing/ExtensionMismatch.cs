namespace Deguffer.Testing;

/// <summary>
/// How an extension record can fail to be what its owner's <c>$ATTRIBUTE_LIST</c> says it is. On a
/// live volume each is a file that changed between the read of its base record and the read of
/// this one.
/// </summary>
public enum ExtensionMismatch
{
    /// <summary>The record was freed and reused since the list was written.</summary>
    ItsOwnSequence,

    /// <summary>The record now belongs to another file.</summary>
    OwnerNumber,

    /// <summary>The record names the owner's record as it was before that record was reused.</summary>
    OwnerSequence,
}
