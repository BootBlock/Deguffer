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

    /// <summary>Whether the directory exists, tolerating paths beyond MAX_PATH.</summary>
    public static bool DirectoryExists(string path) => Directory.Exists(Extended(path));

    /// <summary>Whether the file exists, tolerating paths beyond MAX_PATH.</summary>
    public static bool FileExists(string path) => File.Exists(Extended(path));

    /// <summary>
    /// Whether this is a directory holding at least one entry, tolerating paths beyond MAX_PATH.
    ///
    /// <para>§5.6 asks it of every protected path, so it stops at the first entry rather than
    /// counting them: the question is whether the directory still holds <em>anything</em>, and a
    /// count would walk a Recycle Bin to learn what one entry settles (G4).</para>
    ///
    /// <para>False for a file, for a path that is not there, and for a directory that cannot be
    /// listed. The last of those is the one worth stating: an unreadable directory answers the same
    /// as an empty one, so a protected path Windows will not list is never <em>reported</em> as
    /// having held something. That keeps a refusal out of the evidence rather than turning it into
    /// an alarm, and it is the same direction §5.3 takes everywhere else.</para>
    /// </summary>
    public static bool HoldsAnything(string path)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(Extended(path)).Any();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // Not a directory, gone, or not listable by this account.
            return false;
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
    /// <para>Those eleven do not reach it, because of what it takes to make
    /// <c>GetFileAttributes</c> refuse. NTFS answers out of the parent directory's own index
    /// whenever the caller may list the parent, so denying a directory every right including
    /// <c>FILE_READ_ATTRIBUTES</c> leaves its attributes readable, and so does denying the parent
    /// everything while the directory itself still answers. Only an access rule on both ends
    /// refuses — measured, not reasoned about — and in exactly that condition
    /// <see cref="DirectoryExists"/> answers false, because it is the same query and
    /// <c>Directory.Exists</c> swallows the same failure. All eleven ask that first and take the
    /// absent branch instead.</para>
    ///
    /// <para><b>What they say on that branch is not right either, and it is not this predicate's
    /// to fix.</b> A provider that probed for its cache by name reports "not installed" about a
    /// directory that is on disk with content in it, because the existence check cannot tell absent
    /// from unreadable any more than this one can tell link from unreadable.
    /// <see cref="Providers.UnreadableRoot"/> is the sentence that shape needs, and reaching it
    /// means a three-state answer from <see cref="DirectoryExists"/> across every root probe in
    /// Core. That is its own piece of work; <c>docs/todo/after-the-scanner.md</c> item 8 carries
    /// it.</para>
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
