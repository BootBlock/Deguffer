using System.Buffers;
using System.Text.RegularExpressions;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>What one application's <c>packages</c> folder turned out to hold.</summary>
/// <param name="Superseded">
/// The update packages Squirrel's own index no longer names and which are not newer than the
/// installed build, each with the moment it was downloaded. These are what its prune was supposed
/// to delete and did not.
///
/// <para>The timestamp travels with the path because the enumeration that found the file already
/// carries it. Reading it again would be a second probe per package (G5) — and a worse answer:
/// <see cref="FileInfo.LastWriteTimeUtc"/> answers for a file that has since gone with the start of
/// the Windows epoch rather than by failing, and the updater's own prune may be running over this
/// very folder while the scan reads it. January 1601 beside a pre-selected deletion row is the
/// oldest invitation there is to remove something already gone, which is the case
/// <see cref="DirectoryAge"/> carries the same guard for.</para>
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
/// <summary>
/// What every application's packages folder came to, in the four lists a plan is built from.
///
/// <para>The same shape <see cref="DeclaredLocationScan"/> carries, and for the same reason: a
/// provider adds this to whatever its other locations produced and hands the result to
/// <see cref="Execution.CleanupPlan"/>.</para>
/// </summary>
/// <param name="Targets">The spent packages, ready to be measured.</param>
/// <param name="Protected">What §5.6 asserts survived, with the reason the user is shown.</param>
/// <param name="Notes">What the user is told, including anything left alone and why.</param>
/// <param name="Declined">
/// How many folders were passed over for a reason of Deguffer's own — a link, an index it could not
/// read, an installation it could not order. Counted because a plan with no steps and a decline must
/// not be rendered as "Already clear".
/// </param>
/// <param name="Unreadable">Whether a folder refused to be listed, so its content is unknown.</param>
internal sealed record SquirrelPackageScan(
    IReadOnlyList<DeletionTarget> Targets,
    IReadOnlyList<(string Path, string Reason)> Protected,
    IReadOnlyList<PlanNote> Notes,
    int Declined,
    bool Unreadable);

internal readonly record struct SquirrelPackageReading(
    IReadOnlyList<(string Path, DateTime? LastWritten)> Superseded,
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
    /// The identifier deciding whether this machine gets an application's staged releases early. It
    /// sits in the packages folder and is not a package, which is half of why the folder is never
    /// taken whole.
    /// </summary>
    public const string StagedIdentifierName = ".betaId";

    private const string SupersededReason =
        "An update package this application's own index no longer refers to. Squirrel's clean-up "
        + "was supposed to remove it after the update that replaced it.";

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
    /// Every application's packages folder, and everything beside them that §5.6 must assert
    /// survived.
    ///
    /// <para>The same shape <see cref="DeclaredLocations.Examine"/> uses, for the same reason: what
    /// a provider needs back from a location is a plan's four lists, and assembling them is the
    /// location's own knowledge rather than the provider's. It lives here rather than in the
    /// provider because everything it decides is a fact about this folder — which of its files may
    /// go, which of its neighbours must not, and what to say when its index cannot be read.</para>
    /// </summary>
    /// <param name="installations">The applications found on this machine.</param>
    public static SquirrelPackageScan Examine(
        IReadOnlyList<SquirrelInstallation> installations,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(installations);

        var targets = new List<DeletionTarget>();
        var survivors = new List<(string Path, string Reason)>();
        var notes = new List<PlanNote>();

        var unreadable = false;
        var declined = 0;
        var indexesUnread = 0;
        var unordered = 0;

        foreach (var installation in installations)
        {
            ct.ThrowIfCancellationRequested();

            var packages = Path.Combine(installation.Root, SquirrelDiscovery.PackagesDirectoryName);

            survivors.Add((
                installation.Root,
                $"The folder {installation.Name} is installed in must survive — only spent update "
                + "packages inside it are removed."));
            survivors.Add((
                Path.Combine(installation.Root, SquirrelDiscovery.UpdaterName),
                $"The updater {installation.Name} keeps itself up to date with."));

            // The builds are siblings of the folder this reaches into, which is exactly when an
            // over-broad rule takes one with the other — an assertion that the application's folder
            // survived would pass with every build inside it gone. Removing one is the
            // superseded-versions provider's business and never this one's, at any version.
            survivors.AddRange(installation.Versions.Select(version => (
                version.Path,
                $"A build of {installation.Name}. This row removes update packages and never a "
                + "build.")));

            if (!LongPath.DirectoryExists(packages))
            {
                continue;
            }

            if (LongPath.IsReparsePoint(packages))
            {
                notes.Add(CacheLevelWalk.Note(packages));
                survivors.Add((packages, CacheLevelWalk.LinkReason));
                declined++;
                continue;
            }

            survivors.Add((
                packages,
                $"The folder {installation.Name} keeps its update packages in must survive — only "
                + "the packages it no longer refers to are removed."));
            survivors.Add((
                Path.Combine(packages, IndexName),
                $"{installation.Name}'s own record of the packages it holds. Its shortcut reads this "
                + "file to work out which version to start."));
            survivors.Add((
                Path.Combine(packages, StagedIdentifierName),
                $"The identifier that decides whether this computer gets {installation.Name}'s "
                + "staged releases early."));

            // An installation nobody could order is settled here rather than inside the reading,
            // because the reason belongs to this level. Deciding whether a package is spent means
            // comparing it against the installed build, and there is no installed build to compare
            // against — which is a different fact from an index that would not be read, and folding
            // the two together put a sentence about an unreadable record in front of a user whose
            // record was perfectly readable.
            if (installation.Current is not { } current)
            {
                unordered++;
                declined++;
                continue;
            }

            var reading = Read(packages, current.Number, ct);

            survivors.AddRange(reading.StillNeeded);

            if (reading.DirectoryUnreadable)
            {
                notes.Add(UnreadableRoot.Note(packages));
                unreadable = true;
                continue;
            }

            if (reading.IndexUnreadable)
            {
                indexesUnread++;
                declined++;
                continue;
            }

            foreach (var link in reading.Declined)
            {
                notes.Add(CacheLevelWalk.Note(link));
                declined++;
            }

            targets.AddRange(reading.Superseded.Select(package => new DeletionTarget(
                package.Path, SupersededReason, package.LastWritten, TargetKind.File)));
        }

        // One sentence for all of them, per reason. Which application it was does not change what
        // the user can do about it, and a line per application would bury the rest of the plan on a
        // machine with several.
        if (indexesUnread > 0)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"Left the update packages of {indexesUnread} "
                + (indexesUnread == 1
                    ? "application alone: Deguffer could not read the record of which packages it "
                      + "still needs"
                    : "applications alone: Deguffer could not read the record of which packages "
                      + "they still need")
                + ", and without it a spent package cannot be told from the one the next update is "
                + "built from."));
        }

        if (unordered > 0)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"Left the update packages of {unordered} "
                + (unordered == 1
                    ? "application alone: Deguffer could not work out which build is installed, and "
                      + "a spent package is only spent by comparison with it."
                    : "applications alone: Deguffer could not work out which builds are installed, "
                      + "and a spent package is only spent by comparison with them.")));
        }

        return new SquirrelPackageScan(targets, survivors, notes, declined, unreadable);
    }

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

        var superseded = new List<(string Path, DateTime? LastWritten)>();
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

            // The timestamp the enumeration already carries, rather than a second look at a file
            // the updater may be pruning as this runs.
            superseded.Add((path, file.LastWriteTimeUtc));
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
