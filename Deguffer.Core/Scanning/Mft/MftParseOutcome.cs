namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// What one record turned out to be.
///
/// A bool cannot express this, and the difference is the whole point: a table is full of records
/// that hold nothing for a size scan, and every one of them looks — to a caller reading only
/// success or failure — exactly like a record describing a real file that could not be read. The
/// first is ordinary; the second means the index would total a directory short with nothing to
/// show for it.
/// </summary>
internal enum MftParseOutcome
{
    /// <summary>The record describes an entry, and the tree should hold it.</summary>
    Parsed,

    /// <summary>
    /// Nothing for the tree to hold, and nothing missing either: a free record, one never used, or
    /// an extension record whose base record already carries the file it belongs to.
    /// </summary>
    NotAnEntry,

    /// <summary>
    /// A record in use whose identity this reader cannot reach: it carries an
    /// <c>$ATTRIBUTE_LIST</c> and no name of its own, so its <c>$FILE_NAME</c> lives in an
    /// extension record the parser does not follow. NTFS does this once a file has enough hard
    /// links to overflow its own record, which a system volume is full of.
    ///
    /// <para>Deliberately not <see cref="NotAnEntry"/>, though both are skipped. A free record
    /// holds nothing; this one holds a real file of real size, so the directory above it totals
    /// short. Collapsing the two means a caller cannot tell "there was nothing here" from "there
    /// was something here and I could not place it", and a total that is short with nothing to say
    /// so is the one thing a size scan must not produce.</para>
    ///
    /// <para>Deliberately not <see cref="Unreadable"/> either. That takes a volume down, and this
    /// shape is ordinary rather than damage — refusing it would take the fast path off any machine
    /// with a system volume. Following the attribute list to the extension record is the fix that
    /// costs nothing, and it is <c>docs/todo/after-the-scanner.md</c> item 6.</para>
    /// </summary>
    IdentityElsewhere,

    /// <summary>
    /// A record in use that could not be read — a torn write, a malformed attribute run, or a name
    /// this reader cannot decode. Something exists here and the table will not say what, so every
    /// directory above it would total short.
    /// </summary>
    Unreadable,
}
