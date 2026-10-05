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
    /// A base record whose <c>$ATTRIBUTE_LIST</c> says something the scan needs — its name, the
    /// sizes of its data, or its reparse point — is in another record. NTFS does this once a file's
    /// attributes outgrow one record: a large fragmented file, or one with many hard links.
    ///
    /// <para>Only the parser and <see cref="MftRecordStream"/> see this. The stream reads the other
    /// records once the first pass is over and turns every one of these into
    /// <see cref="Parsed"/> or <see cref="Unreadable"/> before any caller is told of it, so no
    /// caller has a third kind of record to make a policy for.</para>
    /// </summary>
    Continued,

    /// <summary>
    /// A record in use that could not be read — a torn write, a malformed attribute run, a name
    /// this reader cannot decode, or a name in an extension record that was caught mid-change or
    /// damaged. Something exists here and the table will not say where, so any directory on the
    /// volume could total short by it.
    /// </summary>
    Unreadable,
}
