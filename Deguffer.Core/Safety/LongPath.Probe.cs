namespace Deguffer.Core.Safety;

/// <summary>
/// What Windows says is at a path, in the three states <see cref="PathPresence"/> names, with what the
/// same attribute read described of the entry: its kind, whether it is a link, its length and its time.
/// </summary>
public static partial class LongPath
{
    /// <summary>
    /// What Windows says is at <paramref name="path"/>, keeping "nothing is there" apart from
    /// "Windows would not say" — the distinction <see cref="DirectoryExists"/> cannot draw, because
    /// <see cref="Directory.Exists"/> answers false for both. See <see cref="PathPresence"/> for
    /// what that cost.
    ///
    /// <para>Ask this wherever the answer decides what the user is told about a location: a
    /// presence probe, and the early return that says a tool is not installed. The two-state form
    /// below is right everywhere the question is only "is there something here to walk", because a
    /// refusal there is met again by the walk itself.</para>
    /// </summary>
    public static PathPresence ProbeDirectory(string path) => Probe(path, EntryKind.Directory, out _, out _, out _);

    /// <summary>
    /// The same answer, with whether the path is a link taken from the same attribute read rather
    /// than a second identical one. For a caller that needs both of a path, which the walk down a
    /// derived path needs of every segment.
    ///
    /// <para><b>Null where Windows described nothing</b>, which is every case but
    /// <see cref="PathPresence.Present"/>. Nullable rather than false, because false is what every
    /// link check in this codebase reads as "proceed" — so a caller that forgot to settle
    /// <see cref="PathPresence.Refused"/> would be handed the open answer with no diagnostic. It
    /// has to say <c>is true</c>, and saying that about a null is the safe reading by
    /// construction.</para>
    ///
    /// <para>That is deliberately not <see cref="IsReparsePoint"/>'s shape, which fails closed
    /// because it has no way to say "I could not tell". This one has two.</para>
    /// </summary>
    public static PathPresence ProbeDirectory(string path, out bool? isLink)
    {
        var presence = Probe(path, EntryKind.Directory, out var described, out _, out _);

        isLink = Carries(described, FileAttributes.ReparsePoint);

        return presence;
    }

    /// <summary>
    /// The same three-state answer for a file. See <see cref="ProbeDirectory"/>.
    /// </summary>
    public static PathPresence ProbeFile(string path) => Probe(path, EntryKind.File, out _, out _, out _);

    /// <summary>
    /// The same answer for a file, with whether it is a link taken from the same attribute read, as
    /// <see cref="ProbeDirectory(string, out bool?)"/> gives it for a directory and with the same
    /// null.
    /// </summary>
    public static PathPresence ProbeFile(string path, out bool? isLink)
    {
        var presence = Probe(path, EntryKind.File, out var described, out _, out _);

        isLink = Carries(described, FileAttributes.ReparsePoint);

        return presence;
    }

    /// <summary>
    /// The same three-state answer for a path that may be either kind, which is what §5.6 asks of a
    /// protected path. <see cref="PathPresence.Present"/> exactly where
    /// <c>FileExists || DirectoryExists</c> is true, so it answers everything that pair answers and
    /// keeps a refusal apart from an absence as well.
    ///
    /// <para>One attribute read answers for either kind, because planning asks this of every
    /// candidate it holds.</para>
    /// </summary>
    public static PathPresence ProbeEntry(string path) => Probe(path, EntryKind.Either, out _, out _, out _);

    /// <summary>
    /// The same answer, with which kind the entry is taken from the same attribute read, for a caller
    /// that treats a file and a directory differently. Null where Windows described nothing, as
    /// <see cref="ProbeDirectory(string, out bool?)"/> gives its link answer.
    /// </summary>
    public static PathPresence ProbeEntry(string path, out bool? isDirectory) =>
        ProbeEntry(path, out isDirectory, out long _);

    /// <summary>
    /// The same answer, with which kind the entry is and whether it is a link, both taken from the
    /// same attribute read, and null where Windows described nothing, as
    /// <see cref="ProbeDirectory(string, out bool?)"/> gives its link answer.
    ///
    /// <para><b>For a removal, which must answer for a link of either kind.</b> A file symbolic link
    /// at a directory's path is a link to whoever made it, and the directory probe would call it
    /// absent instead. Asking for the kind and the link in two reads lets them describe two entries:
    /// a folder replaced by a junction in between passes one as the folder and the other as the
    /// junction.</para>
    /// </summary>
    public static PathPresence ProbeEntry(string path, out bool? isDirectory, out bool? isLink)
    {
        var presence = Probe(path, EntryKind.Either, out var described, out _, out _);

        isDirectory = Carries(described, FileAttributes.Directory);
        isLink = Carries(described, FileAttributes.ReparsePoint);

        return presence;
    }

    /// <summary>
    /// The same answer, with the newer of the entry's creation and last-write times from the same
    /// attribute read, for a measurement that dates the directory it is about to list (see
    /// <see cref="Scanning.ScanResult.LastWritten"/>).
    /// </summary>
    /// <param name="newestFileTime">
    /// The newer of the two times as a FILETIME, or zero where Windows described nothing.
    /// </param>
    public static PathPresence ProbeEntry(string path, out bool? isDirectory, out long newestFileTime)
    {
        var presence = Probe(path, EntryKind.Either, out var described, out _, out newestFileTime);

        isDirectory = Carries(described, FileAttributes.Directory);

        return presence;
    }

    /// <summary>
    /// <see cref="ProbeDirectory(string)"/>'s answer, with the directory's own time as
    /// <see cref="ProbeEntry(string, out bool?, out long)"/> gives it, from the same attribute read.
    /// </summary>
    public static PathPresence ProbeDirectoryTime(string path, out long newestFileTime) =>
        Probe(path, EntryKind.Directory, out _, out _, out newestFileTime);

    /// <summary>
    /// <see cref="ProbeEntry(string)"/>'s answer, with everything the same attribute read described:
    /// for a measurement handed a path that may be either kind, which sizes a file and dates a
    /// directory from the one read. Default where Windows described nothing.
    /// </summary>
    public static PathPresence ReadEntry(string path, out EntryReading reading)
    {
        var presence = Probe(path, EntryKind.Either, out var described, out var length, out var newestFileTime);

        reading = described is { } attributes ? new EntryReading(attributes, length, newestFileTime) : default;

        return presence;
    }

    /// <summary>
    /// Whether what a probe described carries <paramref name="flag"/>, or null where it described
    /// nothing. A mask rather than <see cref="Enum.HasFlag"/>, which boxes until the JIT optimises
    /// the caller, and every probe of a planning pass comes through here.
    /// </summary>
    private static bool? Carries(FileAttributes? described, FileAttributes flag) =>
        described is { } attributes ? (attributes & flag) != 0 : null;

    /// <param name="expected">
    /// Which kind the caller asked about. The other kind is <see cref="PathPresence.Absent"/> rather
    /// than present: a file where a directory was expected means there is no directory there, which
    /// is the question that was put.
    /// </param>
    /// <param name="described">What Windows described, or null for every answer but present.</param>
    /// <param name="length">The entry's length in bytes, or zero for every answer but present.</param>
    /// <param name="newestFileTime">
    /// The newer of the entry's creation and last-write times as a FILETIME, or zero for every answer
    /// but present.
    /// </param>
    private static PathPresence Probe(
        string path, EntryKind expected, out FileAttributes? described, out long length, out long newestFileTime)
    {
        described = null;
        length = 0;
        newestFileTime = 0;

        // Exactly where the two-state form has always had it. A value Windows will not accept at all
        // throws from here, as it did before, rather than being reported as something about the
        // user's disk.
        var extended = Extended(path);

        // A file cannot be named with a trailing separator, and Win32 answers for the file anyway.
        // File.Exists refuses it, so without this the two-state form's answer would reverse — and a
        // caller that opened what this said was there would meet ERROR_DIRECTORY instead.
        if (expected is EntryKind.File && EndsWithSeparator(extended))
        {
            return PathPresence.Absent;
        }

        var error = FileAttributeRead.Read(extended, out var attributes, out var read, out var newest);

        if (error != 0)
        {
            // Anything but the absent set is an access rule on both ends of the path, or a link
            // Windows will not follow (ERROR_UNTRUSTED_MOUNT_POINT). Neither says anything about
            // what is there.
            return SaysNothingIsThere(error) ? PathPresence.Absent : PathPresence.Refused;
        }

        var isDirectory = (attributes & FileAttributes.Directory) != 0;

        // A file named with a trailing separator is absent for either kind, for the reason above.
        var answersFor = expected switch
        {
            EntryKind.Directory => isDirectory,
            EntryKind.File => !isDirectory,
            _ => isDirectory || !EndsWithSeparator(extended),
        };

        if (!answersFor)
        {
            return PathPresence.Absent;
        }

        described = attributes;
        length = read;
        newestFileTime = newest;

        return PathPresence.Present;
    }

    /// <summary>The kind of entry a probe asks about.</summary>
    private enum EntryKind
    {
        Directory,
        File,
        Either,
    }
}
