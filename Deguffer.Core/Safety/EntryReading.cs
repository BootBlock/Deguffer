namespace Deguffer.Core.Safety;

/// <summary>
/// What one attribute read described of an entry: enough to tell a file from a directory and a link
/// from what it points at, to size a file, and to date either kind, without asking Windows again.
///
/// <para>Here so that a measurement handed a path it cannot classify in advance pays one read for it,
/// whichever kind it turns out to be. Asking for a directory and then for a file read a file twice,
/// once to learn that no directory was there.</para>
/// </summary>
/// <param name="Attributes">The attributes Windows reported.</param>
/// <param name="Length">The length in bytes: a link's own, never its target's, and zero for a directory.</param>
/// <param name="NewestFileTime">
/// The newer of the creation and last-write times as a FILETIME, by
/// <see cref="MinimumAge.NewestFileTimeOf(long, long)"/>: a link's own, never its target's.
/// </param>
public readonly record struct EntryReading(FileAttributes Attributes, long Length, long NewestFileTime)
{
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;

    /// <summary>Whether the entry carries a reparse point: a link, or a mark such as a cloud placeholder's.</summary>
    public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;
}
