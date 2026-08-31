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
    /// A record in use that could not be read — a torn write, a malformed attribute run, or a name
    /// this reader cannot decode. Something exists here and the table will not say what, so every
    /// directory above it would total short.
    /// </summary>
    Unreadable,
}
