namespace Deguffer.Core.Safety;

/// <summary>
/// §6.3: long path support is mandatory. Node and NuGet trees routinely exceed MAX_PATH, and
/// truncating there is the most likely source of a silent partial deletion.
///
/// The app's manifest opts in to <c>longPathAware</c>, but Core is also consumed by a test host
/// that has no such manifest, so every filesystem call in Core goes through the extended-length
/// prefix rather than relying on process-wide configuration.
/// </summary>
public static class LongPath
{
    private const string DevicePrefix = @"\\?\";
    private const string UncDevicePrefix = @"\\?\UNC\";

    /// <summary>
    /// Return <paramref name="path"/> in extended-length form. Requires a rooted, already
    /// normalised path — the Win32 device namespace does no normalisation of its own, so
    /// <c>.</c>, <c>..</c> and relative segments must be resolved before prefixing.
    /// </summary>
    public static string Extended(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (path.StartsWith(DevicePrefix, StringComparison.Ordinal))
        {
            return path;
        }

        var full = Path.GetFullPath(path);

        return full.StartsWith(@"\\", StringComparison.Ordinal)
            ? UncDevicePrefix + full[2..]
            : DevicePrefix + full;
    }

    /// <summary>
    /// A path the user configured, in the form the rest of the code may rely on: fully qualified,
    /// fully resolved, and without a trailing separator. Null when the value is not a full path, or
    /// names something Windows will not accept as one.
    ///
    /// <para>Every caller of this is a provider whose root comes from an environment variable or a
    /// settings file, and two things go wrong when such a value is used as it arrived. A trailing
    /// separator makes <see cref="Path.GetFileName(string)"/> return nothing, so a provider that
    /// splits a root from its leaf declares a target that resolves back to the directory it also
    /// asserts must survive — and §5.6 then reports a correct run as a failure. A value ending in
    /// <c>..</c> is worse: <see cref="Extended"/> requires an already-normalised path, because the
    /// Win32 device namespace resolves nothing, so the deletion would land one directory above the
    /// one the plan named.</para>
    ///
    /// <para><see cref="Path.GetFullPath(string)"/> is safe here only because the value is checked
    /// to be fully qualified first: an unqualified one would resolve against Deguffer's own working
    /// directory, which is a directory nobody pointed at. It resolves nothing in a value that already
    /// carries the device prefix, though, so the prefix comes off first — otherwise a configured
    /// <c>\\?\C:\Users\me\.m2</c> would keep its <c>..</c> segments and would compare equal to
    /// nothing, walking straight past a caller's check that it is not the tool's own directory.</para>
    /// </summary>
    public static string? Configured(string? value)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed) || !Path.IsPathFullyQualified(trimmed))
        {
            return null;
        }

        try
        {
            // A drive root keeps its separator, which is correct: "C:\" is the directory. A UNC root
            // does lose one, and both then have no containing directory at all — which is how the
            // callers that must refuse a whole volume come to refuse it.
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(Display(trimmed)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Characters Windows will not accept in a path, so there is nothing here to point at.
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is <paramref name="ancestor"/> itself or sits inside it.
    ///
    /// <para>Both providers that accept a configured root need this to refuse one that would swallow
    /// the tool's own directory, or one of the things inside it the provider promises to leave
    /// standing. A configured value has been through <see cref="Configured"/>, but the other side of
    /// the comparison is often a <see cref="Path.Combine(string, string)"/> result that has not, so
    /// the separator is handled here rather than assumed.</para>
    ///
    /// <para>A volume root is the case that makes that matter: <c>C:\</c> keeps its separator, and
    /// appending another would build a prefix nothing can match — so a caller asking whether
    /// something is under a whole volume would be told no.</para>
    /// </summary>
    public static bool Contains(string ancestor, string candidate)
    {
        if (candidate.Equals(ancestor, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = ancestor.EndsWith(Path.DirectorySeparatorChar)
            || ancestor.EndsWith(Path.AltDirectorySeparatorChar)
                ? ancestor
                : ancestor + Path.DirectorySeparatorChar;

        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Strip the extended-length prefix, for display and comparison.
    ///
    /// <para><b>Only where what is left is still a path.</b> The prefix comes off
    /// <c>\\?\C:\cache</c> and <c>\\?\UNC\server\share</c> because <c>C:\cache</c> and
    /// <c>\\server\share</c> are the same locations said better. It must not come off
    /// <c>\\?\Volume{…}\cache</c>, which is how Windows names a drive that has no letter: the
    /// remainder is <c>Volume{…}\cache</c>, which is not fully qualified, so anything that hands it
    /// back to <see cref="Extended"/> or <see cref="Configured"/> resolves it against Deguffer's own
    /// working directory — a folder nobody named, silently.</para>
    ///
    /// <para>That is not a display problem, it is a safety one. Such a string reaches
    /// <see cref="Execution.ProtectedPath"/>, and §5.6's negative then asserts the survival of a
    /// path under Deguffer's own directory rather than the one on the drive. It measures absent, it
    /// is reported as "nothing to preserve", and the check passes over whatever really happened.
    /// <c>FileHistoryDiscovery</c> is the first thing in Core that can produce a volume-GUID root,
    /// so the case is reachable rather than theoretical.</para>
    /// </summary>
    public static string Display(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (path.StartsWith(UncDevicePrefix, StringComparison.Ordinal))
        {
            return @"\\" + path[UncDevicePrefix.Length..];
        }

        if (!path.StartsWith(DevicePrefix, StringComparison.Ordinal))
        {
            return path;
        }

        var stripped = path[DevicePrefix.Length..];

        return Path.IsPathFullyQualified(stripped) ? stripped : path;
    }

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
    public static PathPresence ProbeDirectory(string path) => Probe(path, expectDirectory: true, out _);

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
    public static PathPresence ProbeDirectory(string path, out bool? isLink) =>
        Probe(path, expectDirectory: true, out isLink);

    /// <summary>
    /// The same three-state answer for a file. See <see cref="ProbeDirectory"/>.
    /// </summary>
    public static PathPresence ProbeFile(string path) => Probe(path, expectDirectory: false, out _);

    /// <summary>
    /// The same three-state answer for a path that may be either kind, which is what §5.6 asks of a
    /// protected path. <see cref="PathPresence.Present"/> exactly where
    /// <c>FileExists || DirectoryExists</c> is true, so it answers everything that pair answers and
    /// keeps a refusal apart from an absence as well.
    /// </summary>
    public static PathPresence ProbeEntry(string path) => ProbeDirectory(path) switch
    {
        // Asked of the other kind only on an absence. A refusal is the same attribute read failing,
        // and asking again would only fail again.
        PathPresence.Absent => ProbeFile(path),
        var answer => answer,
    };

    /// <summary>
    /// Whether a directory may be at <paramref name="path"/>: true unless Windows said nothing is.
    ///
    /// <para><b>The form a presence probe asks in.</b> A row that does not appear cannot be
    /// corrected by anything downstream, so the one reading a refusal must not remove is the one
    /// that denies the location exists. <see cref="Execution.Finding"/> carries it into the shell,
    /// and the plan behind it then says what was and was not looked at.</para>
    ///
    /// <para>The same rule <see cref="IsReparsePoint"/> and <see cref="IFileSystem.MayExist"/>
    /// already follow, and for the same reason: on a question whose false answer ends the enquiry,
    /// "I cannot tell" has to read as the answer that keeps it open.</para>
    /// </summary>
    public static bool DirectoryMayExist(string path) => ProbeDirectory(path) is not PathPresence.Absent;

    /// <summary>Whether a file may be at <paramref name="path"/>. See <see cref="DirectoryMayExist"/>.</summary>
    public static bool FileMayExist(string path) => ProbeFile(path) is not PathPresence.Absent;

    /// <summary>
    /// Whether the directory exists, tolerating paths beyond MAX_PATH. A path Windows would not
    /// describe answers false, which is what <see cref="Directory.Exists"/> has always done here —
    /// so a caller that cares asks <see cref="ProbeDirectory"/> instead of this.
    /// </summary>
    public static bool DirectoryExists(string path) => ProbeDirectory(path) is PathPresence.Present;

    /// <summary>Whether the file exists, tolerating paths beyond MAX_PATH.</summary>
    public static bool FileExists(string path) => ProbeFile(path) is PathPresence.Present;

    /// <param name="expectDirectory">
    /// Which kind the caller asked about. The other kind is <see cref="PathPresence.Absent"/> rather
    /// than present: a file where a directory was expected means there is no directory there, which
    /// is the question that was put.
    /// </param>
    /// <summary>
    /// The Win32 errors <see cref="Directory.Exists"/> itself treats as "nothing is there", taken
    /// from .NET's own <c>FillAttributeInfo</c> exclusion list.
    ///
    /// <para><b>Copied rather than reasoned about, because matching it exactly is the safety
    /// argument.</b> <see cref="DirectoryExists"/> is read at around a hundred call sites, several
    /// of them guarding a deletion, and it stopped being <c>Directory.Exists</c> when the third
    /// answer arrived. Deciding absence on the same error set is what makes the two answer
    /// identically for every input, so the third answer is added to those call sites and nothing
    /// else is.</para>
    ///
    /// <para>They are also right on their own terms. A share that does not exist, a drive with no
    /// media in it and a value Windows will not accept as a path are complete answers — nothing is
    /// there — and folding one of those into a refusal would put a warning about somebody's disk on
    /// an unplugged card reader. That is the mirror image of the defect this whole probe exists to
    /// end.</para>
    /// </summary>
    private static bool SaysNothingIsThere(int win32Error) => win32Error is
        2 or      // ERROR_FILE_NOT_FOUND
        3 or      // ERROR_PATH_NOT_FOUND
        6 or      // ERROR_INVALID_HANDLE
        15 or     // ERROR_INVALID_DRIVE
        21 or     // ERROR_NOT_READY
        53 or     // ERROR_BAD_NETPATH
        65 or     // ERROR_NETWORK_ACCESS_DENIED
        67 or     // ERROR_BAD_NET_NAME
        87 or     // ERROR_INVALID_PARAMETER
        123 or    // ERROR_INVALID_NAME
        161 or    // ERROR_BAD_PATHNAME
        206 or    // ERROR_FILENAME_EXCED_RANGE
        1231;     // ERROR_NETWORK_UNREACHABLE

    private static PathPresence Probe(string path, bool expectDirectory, out bool? isLink)
    {
        isLink = null;

        // Exactly where the two-state form has always had it. A value Windows will not accept at all
        // throws from here, as it did before, rather than being reported as something about the
        // user's disk.
        var extended = Extended(path);

        // A file cannot be named with a trailing separator, and Win32 answers for the file anyway.
        // File.Exists refuses it, so without this the two-state form's answer would reverse — and a
        // caller that opened what this said was there would meet ERROR_DIRECTORY instead.
        if (!expectDirectory && EndsWithSeparator(extended))
        {
            return PathPresence.Absent;
        }

        var error = FileAttributeRead.Read(extended, out var attributes);

        if (error != 0)
        {
            // Anything but the absent set is an access rule on both ends of the path, or a link
            // Windows will not follow (ERROR_UNTRUSTED_MOUNT_POINT). Neither says anything about
            // what is there.
            return SaysNothingIsThere(error) ? PathPresence.Absent : PathPresence.Refused;
        }

        if (attributes.HasFlag(FileAttributes.Directory) != expectDirectory)
        {
            return PathPresence.Absent;
        }

        isLink = attributes.HasFlag(FileAttributes.ReparsePoint);

        return PathPresence.Present;
    }

    private static bool EndsWithSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Whether this path is a junction or symbolic link rather than a real directory.
    ///
    /// <see cref="DirectoryExists"/> answers true for a junction and says nothing about it, so a
    /// target reached by name rather than by <see cref="ChildDirectories.Under"/> needs this to
    /// uphold the same rule: a link points at a tree the caller never classified, and deleting
    /// through it leaves the tree the plan described.
    ///
    /// <para>Every caller reads false as "proceed", so this fails closed. A path that is not there
    /// is genuinely not a reparse point. A path we were refused, could not read, or could not even
    /// parse is not an answer at all, and the only safe reading of "I cannot tell" on a predicate
    /// guarding a deletion is the one that stops it.</para>
    ///
    /// <para><b>No caller renders the closed answer as a link</b>, and that is worth stating
    /// because eleven of them turn a true into the sentence "it is a link to somewhere else" — a
    /// specific claim about the machine, where the truth would be that Deguffer could not tell.
    /// Rendering a non-answer as a fact is the defect <see cref="ChildDirectories.Under"/> was
    /// corrected for, and it would be worse here.</para>
    ///
    /// <para>Those eleven do not reach it because <see cref="ProbeDirectory"/> answers first, and
    /// a <see cref="PathPresence.Refused"/> root is reported as one before anything asks about
    /// links. Whatever makes <c>GetFileAttributes</c> refuse — an access rule on both ends of the
    /// path, or a link Windows will not follow — that probe meets it and says so, so the only
    /// answer reaching here is one Windows already gave. The argument used to run the other way:
    /// the gate ahead of this one failed on the same condition and sent every provider down the
    /// "not installed" branch, which was a coincidence holding a safety property up. It is not a
    /// coincidence now.</para>
    ///
    /// <para><b>A caller that reads a true as a reason to do <em>less</em> asks the probe first
    /// too.</b> <see cref="Execution.RefusalCheck"/> asks this of a step's root and of each place it
    /// checks, where a true means "nothing here will be refused", and <c>ExploreRemover</c> asks it
    /// before searching a folder for an Outlook store, where a true skips the search. Asked
    /// unprobed, each would turn a path Windows would not describe into a smaller claim than the
    /// truth: a preview promising back a figure nobody could check, and a folder moved to the Recycle
    /// Bin unexamined. So each settles <see cref="PathPresence.Refused"/> first and says so.</para>
    ///
    /// <para><b>Three callers do <em>not</em> ask first, and each is answered by what it does with a
    /// true.</b> <see cref="BuildDirectorySignature"/> and <see cref="DotNetIntermediateSignature"/>
    /// put a candidate and its parent through this without probing either, and what they say when
    /// they get a true is "not recognised as build output, so it is left alone" — §5.2's own answer
    /// for a thing that could not be classified, which names no link and claims nothing.
    /// <see cref="Execution.FileRemover"/> asks this before anything else, through the
    /// <see cref="IFileSystem"/> seam, and a true there removes the path as a link and reports
    /// nothing reclaimed rather than its length. That under-reports, which is the safe
    /// direction.</para>
    ///
    /// <para><see cref="DotNetIntermediateSignature"/> carried its own copy of this rule and now
    /// calls here instead. A safety predicate written twice is one that gets changed once.</para>
    /// </summary>
    public static bool IsReparsePoint(string path)
    {
        string extended;

        try
        {
            extended = Extended(path);
        }
        catch (ArgumentException)
        {
            return true;
        }

        return FileAttributeRead.Read(extended, out var attributes) switch
        {
            0 => attributes.HasFlag(FileAttributes.ReparsePoint),
            FileAttributeRead.FileNotFound or FileAttributeRead.PathNotFound => false,
            _ => true,
        };
    }
}
