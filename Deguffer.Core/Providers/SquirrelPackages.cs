using System.Buffers;
using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>What one application's <c>packages</c> folder turned out to hold.</summary>
/// <param name="Superseded">
/// The update packages Squirrel's own index no longer names and which are not newer than the
/// installed build. These are what its prune was supposed to delete and did not.
/// </param>
/// <param name="StillNeeded">
/// Every package file left alone, with the reason, for §5.6. It holds the ones the index still
/// names — the base a delta update is applied to — and any whose name could not be read.
/// </param>
/// <param name="Declined">
/// Package files that turned out to be links. Reported apart from <paramref name="StillNeeded"/>,
/// which holds them too, because a link is something Deguffer looked at and refused rather than
/// something it found nothing of — and the plan owes the user that sentence. Removing one would
/// take a link whose far side nobody classified, and the figure beside it was measured through it.
/// </param>
/// <param name="DirectoryUnreadable">
/// The folder would not be listed, so the two lists above describe nothing. A caller must not read
/// that as "there is nothing in there".
/// </param>
/// <param name="IndexUnreadable">
/// <c>RELEASES</c> is missing, unreadable, or holds a line this could not parse — so nothing in the
/// folder is offered. See <see cref="SquirrelPackages"/> for why that is the only safe answer.
/// </param>
internal readonly record struct SquirrelPackageReading(
    IReadOnlyList<string> Superseded,
    IReadOnlyList<(string Path, string Reason)> StillNeeded,
    IReadOnlyList<string> Declined,
    bool DirectoryUnreadable,
    bool IndexUnreadable);

/// <summary>
/// Squirrel's own record of which update packages it still needs, read rather than guessed at.
///
/// <para><b>The folder is never removed whole, and that is a correction to the obvious design.</b>
/// <c>packages\RELEASES</c> is read with an unguarded <c>File.ReadAllText</c> by
/// <c>Update.exe --processStart</c>, which is the shortcut style Squirrel's own install
/// documentation gives and which several shipped applications use — so a missing index does not
/// degrade, it throws, and the shortcut stops launching the application. <c>.betaId</c> beside it
/// is the identifier that decides whether this machine gets a staged release early. Both are
/// configuration living next to a cache, which is §5.2 exactly.</para>
///
/// <para><b>So the index decides.</b> Squirrel rewrites <c>RELEASES</c> to a single entry after
/// every update and deletes every package file it no longer names — but it does so in an unguarded
/// loop, and one failure abandons the rest. A package the index does not name is what that loop was
/// supposed to remove. On the machine this was measured on, one application was holding a full
/// package for a build 247 versions behind the one installed.</para>
///
/// <para><b>A package newer than the installed build is left alone even when the index does not
/// name it.</b> Squirrel downloads an update into this folder <em>before</em> it rewrites the index,
/// so an update part-way through downloading is unnamed and is not debris. That is the
/// <c>steamapps\downloading</c> trap, and the version comparison is what keeps this out of it.</para>
///
/// <para><b>Anything unreadable means nothing is offered.</b> Without the index there is no way to
/// tell a spent package from the base the next delta update is applied to, and §5.2 requires the
/// unknown case to fail towards leaving things alone.</para>
/// </summary>
internal static partial class SquirrelPackages
{
    /// <summary>Squirrel's index of the packages it holds, inside the folder that holds them.</summary>
    public const string IndexName = "RELEASES";

    /// <summary>
    /// One line of that index: a SHA-1, the file name, and the size. Squirrel's own parser, with the
    /// same shape and the same strictness — a line that does not match makes Squirrel throw, and it
    /// makes this report the index unreadable.
    /// </summary>
    [GeneratedRegex(@"\A([0-9a-fA-F]{40})\s+(\S+)\s+([0-9]+)\s*\z", RegexOptions.CultureInvariant)]
    private static partial Regex IndexEntry();

    /// <summary>A comment, which Squirrel strips before it parses a line.</summary>
    [GeneratedRegex(@"\s*#.*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Comment();

    /// <summary>
    /// The version in a package file name: <c>GitHubDesktop-3.6.4-delta.nupkg</c>. Numeric only, on
    /// <see cref="SquirrelDiscovery"/>'s reasoning — a pre-release version cannot be ordered against
    /// a release without choosing a reading, and the comparison here decides whether a file is a
    /// download in progress.
    /// </summary>
    [GeneratedRegex(
        @"\A.+-(?<version>[0-9]+(?:\.[0-9]+){1,3})(?:-full|-delta)?\.nupkg\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PackageFile();

    private const string StillNamedReason =
        "The update package this application was installed from. Squirrel applies the next update "
        + "as a patch against it.";

    private const string UnreadableNameReason =
        "Deguffer could not read a version out of this package's name, so it cannot tell whether it "
        + "is spent or is an update part-way through downloading.";

    private const string NewerReason =
        "A package for a build newer than the one installed, so it may be an update part-way "
        + "through downloading.";

    /// <summary>
    /// The characters Windows will not accept in a file name, as the set the index check tests
    /// against. <see cref="Path.GetInvalidFileNameChars"/> clones its array on every call so a
    /// caller cannot mutate it, and the check runs once per line of the index (G5).
    /// </summary>
    private static readonly SearchValues<char> InvalidInFileName =
        SearchValues.Create(Path.GetInvalidFileNameChars());

    /// <summary>
    /// Read <paramref name="packagesDirectory"/> against its own index.
    /// </summary>
    /// <param name="packagesDirectory">The folder, which the caller has established is not a link.</param>
    /// <param name="installed">
    /// The newest installed version, which nothing newer than may be offered.
    ///
    /// <para>Not nullable, and that is a correction. An installation whose builds could not be
    /// ordered has no answer here either, but it is a different fact from an index that could not be
    /// read — and folding the two together made the plan tell the user Deguffer could not read a
    /// record it had read perfectly well. The caller owns that case, because the caller is what
    /// knows why.</para>
    /// </param>
    public static SquirrelPackageReading Read(
        string packagesDirectory,
        Version installed,
        CancellationToken ct = default)
    {
        var named = NamedByIndex(packagesDirectory);

        if (named is null)
        {
            return new SquirrelPackageReading([], [], [], DirectoryUnreadable: false, IndexUnreadable: true);
        }

        List<FileInfo> files;

        try
        {
            files = [.. new DirectoryInfo(LongPath.Extended(packagesDirectory)).EnumerateFiles("*.nupkg")];
        }
        catch (DirectoryNotFoundException)
        {
            // Not there is a complete answer: a folder that does not exist holds no packages. The
            // caller checked existence, so this is the folder having gone since.
            return new SquirrelPackageReading([], [], [], DirectoryUnreadable: false, IndexUnreadable: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Nothing rather than a partial view, on ChildDirectories' reasoning: half a listing
            // invites a plan that describes a folder nobody fully read.
            return new SquirrelPackageReading([], [], [], DirectoryUnreadable: true, IndexUnreadable: false);
        }

        var superseded = new List<string>();
        var kept = new List<(string Path, string Reason)>();
        var declined = new List<string>();

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var path = LongPath.Display(file.FullName);

            // Before anything is classified. A file enumeration hands back a link exactly as it
            // hands back a file, so this is the check ChildDirectories makes for a caller that
            // enumerates directories, and DeclaredLocations makes for the only other route in this
            // project that produces a file target. Without it the one enumeration in the change that
            // ends in a deletion is the one that would follow a redirection nobody classified.
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                declined.Add(path);
                kept.Add((path, CacheLevelWalk.LinkReason));
                continue;
            }

            if (named.Contains(file.Name))
            {
                kept.Add((path, StillNamedReason));
                continue;
            }

            // TryParse rather than Parse, for the reason SquirrelDiscovery gives: the pattern bounds
            // a version's shape and not its magnitude, so App-9999999999.0.nupkg matches and then
            // overflows — and an exception here takes down the whole planning pass. A name nobody
            // could read is already a case this keeps, so failing into it fails closed.
            if (PackageFile().Match(file.Name) is not { Success: true } match
                || !Version.TryParse(match.Groups["version"].ValueSpan, out var version))
            {
                kept.Add((path, UnreadableNameReason));
                continue;
            }

            if (version > installed)
            {
                kept.Add((path, NewerReason));
                continue;
            }

            superseded.Add(path);
        }

        return new SquirrelPackageReading(
            superseded, kept, declined, DirectoryUnreadable: false, IndexUnreadable: false);
    }

    /// <summary>
    /// The file names Squirrel's index still refers to, or null where the index could not be read.
    ///
    /// <para>Null and an empty set are different answers and must stay so. An index naming nothing
    /// would make every package in the folder removable, which is exactly what an unreadable index
    /// must not be allowed to mean.</para>
    /// </summary>
    private static HashSet<string>? NamedByIndex(string packagesDirectory)
    {
        string text;

        try
        {
            // The same encoding Squirrel reads it with, so the byte-order mark a real RELEASES
            // carries is stripped here exactly as it is there.
            text = File.ReadAllText(
                LongPath.Extended(Path.Combine(packagesDirectory, IndexName)), System.Text.Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Missing, held open, or on a path this account may not read. All three mean the same
            // thing: nothing established which packages are still needed.
            return null;
        }

        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in text.Split('\n'))
        {
            var entry = Comment().Replace(line, string.Empty).Trim();

            if (entry.Length == 0)
            {
                continue;
            }

            if (IndexEntry().Match(entry) is not { Success: true } match)
            {
                // Squirrel throws on a line it cannot parse, so a file with one in it is not an
                // index either of us can act on.
                return null;
            }

            var name = match.Groups[2].Value;

            // A local index holds bare file names. Squirrel's own parser also accepts an absolute
            // HTTP URL, which belongs to a remote feed rather than to this folder — meeting one
            // here means the file is not what it was taken for, so nothing in the folder is offered.
            if (name.AsSpan().IndexOfAny(InvalidInFileName) >= 0)
            {
                return null;
            }

            named.Add(name);
        }

        return named;
    }
}
