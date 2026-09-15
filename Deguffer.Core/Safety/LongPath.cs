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
    public static PathPresence ProbeDirectory(string path) => Probe(path, expectDirectory: true);

    /// <summary>
    /// The same three-state answer for a file. See <see cref="ProbeDirectory"/>.
    /// </summary>
    public static PathPresence ProbeFile(string path) => Probe(path, expectDirectory: false);

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
    private static PathPresence Probe(string path, bool expectDirectory)
    {
        // Outside the try, exactly where the two-state form has always had it. A path Windows will
        // not accept as one is a caller's mistake, not an answer Windows declined to give, and
        // reporting it as a refusal would put a warning about somebody's disk on a bug in Deguffer.
        var extended = Extended(path);

        try
        {
            return File.GetAttributes(extended).HasFlag(FileAttributes.Directory) == expectDirectory
                ? PathPresence.Present
                : PathPresence.Absent;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return PathPresence.Absent;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // An access rule on both ends of the path, or a link Windows will not follow. Neither
            // says anything about what is there.
            return PathPresence.Refused;
        }
    }

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
    /// <para>Three callers do <em>not</em> ask first. <see cref="BuildDirectorySignature"/> and
    /// <see cref="DotNetIntermediateSignature"/> put a candidate and its parent through this
    /// without probing either, and what they say when they get a true is "not recognised as build
    /// output, so it is left alone" — §5.2's own answer for a thing that could not be classified,
    /// which names no link and claims nothing. <see cref="Execution.FileRemover"/> asks this before
    /// anything else, through the <see cref="IFileSystem"/> seam, and a true there removes the path
    /// as a link and reports nothing reclaimed rather than its length. That under-reports, which is
    /// the safe direction, and it is the one place the closed answer still costs something.</para>
    ///
    /// <para><see cref="DotNetIntermediateSignature"/> carried its own copy of this rule and now
    /// calls here instead. A safety predicate written twice is one that gets changed once.</para>
    /// </summary>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            var attributes = File.GetAttributes(Extended(path));

            return attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
        {
            return true;
        }
    }
}
