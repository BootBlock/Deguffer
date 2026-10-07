using Deguffer.Core.Providers;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Exploring.Acting;

/// <summary>
/// What Explore will and will not remove (§7.1).
///
/// <para>A decision, not a menu. It lives in Core for the reason <see cref="Execution.ElevationOffer"/>
/// and <see cref="Execution.ConfirmationRequirement"/> do: what Explore refuses has to be provable
/// without a WinUI host, and a rule that only exists as a disabled context-menu item is a rule
/// nothing can test.</para>
///
/// <para>It decides in two passes, because the two kinds of refusal come from different places.
/// The first is <see cref="ProtectedRegions"/>, a table of regions — the operating system's own directories, the signed-in user's
/// profile and Outlook's own folder — plus what Windows reserves at the top of any volume, which
/// <see cref="VolumeReservations"/> names and which is read from where the path's volume is mounted
/// rather than from a list of drives. Apart from Outlook's folder, all of that is a fact about
/// Windows and is stated beside this policy. The second is
/// §5.2, which is a fact about a tool and belongs to whichever provider knows the tool: Explore
/// reads it through <see cref="ToolRoot"/> rather than restating it, because a safety rule written
/// twice is one that gets changed once.</para>
///
/// <para>Both passes answer from above, about what contains the path. A path they allow is then
/// asked about from below: removing a folder removes everything in it, so a folder holding something
/// either pass refuses is refused as well. <see cref="HeldLocations"/> asks that, and says why it
/// asks the disk.</para>
///
/// <para>Outlook's mail stores are also refused by type, before the table is asked, because a
/// <c>.pst</c> is wherever somebody saved it. <see cref="OutlookDataFiles"/> holds that rule and
/// Outlook's folder, and says why both are §9's.</para>
///
/// <para><b>Refusal is about removal, and nothing else.</b> Opening a file, showing it in Explorer
/// and putting the Windows properties sheet on screen change nothing on disk, and refusing to open
/// a folder that Explorer will open anyway would be theatre rather than safety. §7.1's rules govern
/// what Explore <em>acts on</em>, and the acting it constrains is the deletion.</para>
/// </summary>
public sealed class ExploreActionPolicy
{
    private static readonly char[] Separators =
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    /// <summary>
    /// How many providers <see cref="ForAsync"/> asks at once (G4). Not derived from the processor
    /// count, because what the handful of probing providers wait on is a subprocess or the process
    /// table rather than this machine's cores — and a user with every toolchain installed should not
    /// see ten console windows' worth of work start at the same instant.
    /// </summary>
    private const int Discovery = 8;

    /// <summary>
    /// The names NTFS reserves in a volume's root directory, from <c>[MS-FSCC]</c>. See
    /// <see cref="ReservedByTheFilesystem"/> for why they are refused and why the set stops here.
    /// </summary>
    private static readonly HashSet<string> NtfsReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "$MFT", "$MFTMirr", "$LogFile", "$Volume", "$AttrDef", "$Bitmap",
        "$Boot", "$BadClus", "$Secure", "$UpCase", "$Extend",
    };

    private readonly RegionTable _regions;
    private readonly IReadOnlyList<DeclaredRoot> _toolRoots;
    private readonly IReadOnlyList<DeclaredRoot> _probedRoots;
    private readonly HeldLocations _held;
    private readonly IVolumeInventory _volumes;
    private readonly IFileSystem _fileSystem;

    /// <param name="regions">
    /// The structural table. Its most-specific-wins rule is <see cref="RegionTable"/>'s, rather than
    /// trusted to the caller's order, because that rule is what makes an exception expressible and an
    /// unordered table would resolve by declaration order instead — silently, and differently for
    /// each caller.
    /// </param>
    /// <param name="toolRoots">The §5.2 declarations, as the providers wrote them.</param>
    /// <param name="volumes">
    /// Where <see cref="VolumeRoot"/> asks which volume a path is on, so that what sits at the top
    /// of a volume mounted at a folder is recognised as surely as what sits at the top of a drive,
    /// and where each region, root and refused location is followed to every path it is reachable
    /// at. The item asked about is followed at each <see cref="MayRemove"/> rather than once here,
    /// which is what keeps a volume mounted after this policy was built covered. Required rather than
    /// defaulted, because a default would be the real machine's volumes, and a test that forgot to
    /// pass a fake would quietly be asking about the developer's disks.
    /// </param>
    /// <param name="fileSystem">
    /// Where <see cref="HeldLocations"/> asks whether a refused location is on disk, and where
    /// <see cref="ToolRootChildren"/> asks what a tool root's child is. Injected so a test can see
    /// the form of the path each is asked about (§6.3), and what a probe that fails does.
    /// </param>
    /// <param name="probedRoots">
    /// What the providers declared once they had asked the machine, through
    /// <see cref="ICleanupProvider.DiscoverToolRootsAsync"/>. Kept apart from
    /// <paramref name="toolRoots"/> because it is asked separately and can only narrow what the rest
    /// allows. <see cref="MayRemove"/> says why.
    /// </param>
    public ExploreActionPolicy(
        IEnumerable<ProtectedRegion> regions,
        IEnumerable<ToolRoot> toolRoots,
        IVolumeInventory volumes,
        IFileSystem? fileSystem = null,
        IEnumerable<ToolRoot>? probedRoots = null)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(toolRoots);
        ArgumentNullException.ThrowIfNull(volumes);

        _regions = new RegionTable(regions, volumes);
        _toolRoots = DeclaredRoot.Follow(toolRoots, volumes);
        _probedRoots = DeclaredRoot.Follow(probedRoots ?? [], volumes);

        _fileSystem = fileSystem ?? WindowsFileSystem.Default;

        // A permitting region protects nothing, and a root that will not resolve names nothing.
        _held = new HeldLocations(
            [
                .. _regions.Refusing,
                .. _toolRoots.Concat(_probedRoots).Select(root => (root.Root.Reason, root.Folder)),
            ],
            _fileSystem);

        _volumes = volumes;
    }

    /// <summary>
    /// The policy for this machine: Windows' own directories, the signed-in user's profile, Outlook's
    /// mail stores, and every §5.2 declaration the providers make. What sits at the top of a volume
    /// is decided by asking <paramref name="volumes"/> where the path's volume is mounted, at the
    /// moment of each question, so no list of drives has to be kept current.
    ///
    /// <para>Assembled from the two seams rather than from <see cref="Environment"/> directly, so
    /// the whole of §7.1's refusal set is provable against a synthetic profile — which is what G1's
    /// dependency inversion is for, and the only way these assertions can run on a machine where
    /// nobody may delete anything in <c>C:\Windows</c>.</para>
    ///
    /// <para><b>Asynchronous, and the only factory over providers, because half of §5.2 is not
    /// knowable without asking the machine.</b> A tool reports a location the documented default does
    /// not name, and a plan holds a path back because something is using it; both are read through
    /// <see cref="ICleanupProvider.DiscoverToolRootsAsync"/>. A second factory that skipped them would
    /// build a policy that looks complete, refuses less, and says nothing about the difference. The
    /// constructor still takes declarations directly, for a caller that already holds them.</para>
    ///
    /// <para>The providers are asked in parallel, bounded, because most of them answer immediately
    /// and the handful that do not are each waiting on a subprocess or the process table (G4).</para>
    /// </summary>
    public static async Task<ExploreActionPolicy> ForAsync(
        ISystemDirectories system,
        IUserEnvironment environment,
        IVolumeInventory volumes,
        IEnumerable<ICleanupProvider> providers,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentNullException.ThrowIfNull(providers);

        IReadOnlyList<ICleanupProvider> asked = [.. providers];
        var discovered = new IReadOnlyList<ToolRoot>[asked.Count];

        await Parallel.ForAsync(
            0,
            asked.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Discovery, CancellationToken = ct },
            async (index, token) =>
                discovered[index] = await asked[index].DiscoverToolRootsAsync(token).ConfigureAwait(false))
            .ConfigureAwait(false);

        return new ExploreActionPolicy(
            ProtectedRegions.For(system, environment),
            [.. asked.SelectMany(p => p.ToolRoots)],
            volumes,
            probedRoots: [.. discovered.SelectMany(roots => roots)]);
    }

    /// <summary>
    /// Whether Explore may remove <paramref name="path"/>, and what to tell the user either way.
    ///
    /// <para>Asked again inside <see cref="ExploreRemover"/> immediately before anything is
    /// deleted, and that repetition is deliberate: a shell that forgot to ask, or asked about the
    /// row it had highlighted rather than the one it went on to delete, would otherwise be the only
    /// thing standing between a size picture and <c>C:\Windows</c>.</para>
    /// </summary>
    public ExploreVerdict MayRemove(string path)
    {
        // Normalised first, because every comparison below is a prefix match on a path's text, made
        // of each path the item and each rule are reachable at. A path
        // carrying '..' compares equal to nothing and would walk straight past the whole table —
        // the same trap LongPath.Configured exists for on a provider's configured root.
        if (LongPath.Configured(path) is not { } target)
        {
            return ExploreVerdict.Refuse(
                "Deguffer could not make sense of that path, so it will not act on it.");
        }

        // A whole volume, whether a drive or a folder one is mounted at, or something with no
        // containing directory at all. None of them is a thing to remove, and asking where the volume
        // is mounted now rather than a list of drives means a volume mounted after this policy was
        // built is covered exactly as one mounted before it.
        if (VolumeRoot.Places(_volumes, target) is not { } places)
        {
            return ExploreVerdict.Refuse(
                $"'{target}' is a whole drive. Explore removes things from a drive, never the drive itself.");
        }

        // Each rule at every place the item is reachable and on every reading of each, because a
        // refusal that holds at any of them holds: VolumeRoot says why the drive letter's reading is
        // kept beside the mount point's, and why the volume's other mounts are asked about.
        IReadOnlyList<string> readings = [.. places.SelectMany(place => place.Readings)];

        if (OnAnyReading(readings, ReservedByTheFilesystem) is { } filesystem)
        {
            return filesystem;
        }

        if (OnAnyReading(readings, InARecycleBin) is { } bin)
        {
            return bin;
        }

        if (OnAnyReading(readings, VolumeReservations.Refusal) is { } reserved)
        {
            return reserved;
        }

        // Every path the item is reachable at, comparable with each region, root and refused location,
        // which were followed to every path they are reachable at when the policy was built. A rule
        // named through another letter or another mount of its volume is found at the path it shares
        // with the item.
        var folder = ReachedFolder.Following(target, places);
        var children = new ToolRootChildren(_fileSystem);
        ExploreVerdict? allowed = null;

        foreach (var place in folder.Places)
        {
            var verdict = Above(place, children);

            if (!verdict.IsAllowed)
            {
                return verdict;
            }

            // The first place's, which is the path the user named, so what they are told about it
            // is never a sentence written about another of its paths.
            allowed ??= verdict;
        }

        // Last, and only of a path everything above allows at every place, because it reads every
        // location the path holds. The tool roots read one entry each, and only of a path inside one.
        return _held.Refusal(target, folder) ?? allowed!;
    }

    /// <summary>
    /// What everything that answers from above says about one place the item is reachable at: the
    /// Outlook rule, the region table, and §5.2's declared and probed roots, in that order.
    /// </summary>
    /// <param name="target">One path the item is reachable at, in <see cref="ReachedFolder.Comparable"/> form.</param>
    private ExploreVerdict Above(string target, ToolRootChildren children)
    {
        // Before the region table, because the table ends in a permission: a mail store inside the
        // signed-in profile would otherwise be answered by the profile's own entry and allowed.
        if (OutlookDataFiles.Refusal(target) is { } mail)
        {
            return mail;
        }

        var verdict = _regions.Innermost(target) is { Verdict.IsAllowed: false } refusing
            ? refusing.Verdict
            : Below(_toolRoots, target, children);

        // A probed declaration is asked only about what everything above allows, so it can add a
        // refusal and never lift one. Its roots come from what a tool reports and what a setting
        // names, and pooled with the declared roots a Maven setting naming 'settings-security.xml'
        // made a root beside Maven's own that recognised the master-password file, and allowed it.
        return verdict.IsAllowed && ProbedRefusal(target, children) is { } probed ? probed : verdict;
    }

    /// <summary>
    /// NTFS's own records, which §7.1 puts out of reach: they are live filesystem state, so the tier
    /// model calls them Tier 4, and Explore "refuses whatever the tier model would call Tier 4, and
    /// it does not get to decide what that is".
    ///
    /// <para>Separate from <see cref="VolumeReservations"/> and not folded into it, because the two
    /// sets come from different owners. What Windows keeps at the top of a volume changes with
    /// Windows, and this set is closed by the filesystem's specification. Both ask about the first
    /// segment below the root, because NTFS's optional features live a level down in
    /// <c>$Extend</c>.</para>
    ///
    /// <para>They are refused at all because §5.5's file-table route <em>draws</em> them. A walk
    /// never sees these names — Windows hides the reserved records from directory enumeration — but
    /// reading the table directly puts <c>$MFT</c> at the top of a scanned drive at several hundred
    /// megabytes, which is exactly the shape of thing a size picture invites somebody to act on.
    /// Offering a deletion the filesystem will refuse teaches a user that saying yes is how you find
    /// out what happens, and §7.1 wants the reason stated instead.</para>
    ///
    /// <para>The set is closed and comes from the filesystem's own specification rather than from
    /// observation, so it needs no maintenance: <c>[MS-FSCC]</c> names what NTFS reserves in a
    /// volume's root directory. It is deliberately <em>not</em> every name beginning with <c>$</c>.
    /// <c>$Recycle.Bin</c> is Windows' rather than NTFS's and is refused below for its own reason,
    /// and <c>$WinREAgent</c> and <c>$Windows.~BT</c> are ordinary leftovers a user may legitimately
    /// want gone — refusing those would take away a capability rather than add a protection.</para>
    /// </summary>
    private static ExploreVerdict? ReservedByTheFilesystem(string below) =>
        below.Split(Separators, StringSplitOptions.RemoveEmptyEntries) is [var first, ..]
        && NtfsReserved.Contains(first)
            ? ExploreVerdict.Refuse(
                $"'{first}' is part of NTFS itself rather than something stored on the drive — it is "
                + "how the filesystem records where every other file is. Windows does not let it be "
                + "deleted, and the space it holds is not recoverable while the drive is in use.")
            : null;

    /// <summary>
    /// A volume's Recycle Bin and everything in it.
    ///
    /// <para>Everything in it, not only the folder, because the bin holds a folder for each account
    /// that has deleted something on the drive. <see cref="Providers.RecycleBinProvider"/> empties
    /// this user's own and names every other one as a path that must survive, and §7.1 refuses every
    /// such path. Refusing the bin alone left another account's deleted files one level down, and
    /// removable wherever Deguffer runs elevated. This user's own is refused too: the Storage page is
    /// where it is emptied, and a deleted file is two entries there, its contents and the record of
    /// where it came from, so removing either leaves a file the bin cannot put back.</para>
    ///
    /// <para>By the first segment below the volume root, as <see cref="ReservedByTheFilesystem"/>
    /// asks, so a folder somebody named <c>$Recycle.Bin</c> inside their own documents stays
    /// theirs.</para>
    /// </summary>
    private static ExploreVerdict? InARecycleBin(string below) =>
        below.Split(Separators, StringSplitOptions.RemoveEmptyEntries) is [var first, ..]
        && first.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
            ? ExploreVerdict.Refuse(
                "This is the drive's Recycle Bin, where each account on this computer keeps what it "
                + "deleted. Emptying yours is offered on the Storage page, where Deguffer can tell your "
                + "own deleted files from another account's.")
            : null;

    private static ExploreVerdict? OnAnyReading(
        IReadOnlyList<string> readings, Func<string, ExploreVerdict?> rule)
    {
        foreach (var below in readings)
        {
            if (rule(below) is { } refusal)
            {
                return refusal;
            }
        }

        return null;
    }

    /// <summary>
    /// §5.2, asked of the <em>innermost</em> tool root containing this path, or the unclassified
    /// answer when none of them does.
    ///
    /// <para>Innermost, not first, and the difference is the whole of the nested case. A provider
    /// whose caches sit below its root declares a root per level — Cargo's <c>.cargo</c>,
    /// <c>.cargo\registry</c> and <c>.cargo\git</c>, and Chromium's user-data folder and each
    /// profile under it — because §5.2's declaration is an allow-list over one directory's
    /// <em>immediate</em> children and cannot reach deeper. Asking the outermost root about
    /// <c>.cargo\registry\cache</c> asks it about <c>registry</c>, which that level declares Tier 4
    /// precisely so that only what is named inside it goes. The answer would be a refusal, and it
    /// would refuse the one directory the provider removes.</para>
    ///
    /// <para>It is also the ordering the region table above uses, for the same reason: a rule and
    /// the narrower rule inside it are both true, and the narrower one is the one that was written
    /// about this path.</para>
    ///
    /// <para><b>Innermost is a set rather than one root, because a directory can have two owners.</b>
    /// A VS Code user-data folder holds Chromium's nine engine caches and the editor's own, so
    /// <see cref="Providers.ChromiumCacheProvider"/> and
    /// <see cref="Providers.VsCodeCacheProvider"/> both declare that one path with disjoint child
    /// tables, and <see cref="Providers.VsCodeLogProvider"/> and
    /// <see cref="Providers.ChromiumServiceWorkerStorageProvider"/> declare it again. Asking only
    /// the first of them would refuse every child the others recognise — silently, and
    /// differently depending on the order the providers happen to be constructed in. So every
    /// declaration at that depth is asked, and a child one of them recognises is allowed: each
    /// provider states what it knows, and none has to carry another's table.</para>
    /// </summary>
    private static ExploreVerdict Below(IReadOnlyList<DeclaredRoot> roots, string target, ToolRootChildren children)
    {
        List<DeclaredRoot> innermost = [];
        int? levels = null;

        foreach (var root in roots)
        {
            if (root.Folder.LevelsTo(target) is not { } below || below > levels)
            {
                continue;
            }

            // Strictly deeper discards what was found before it; equally deep joins it. Two roots
            // that both hold this folder as many levels up are the same folder, however each is named.
            if (below != levels)
            {
                innermost.Clear();
                levels = below;
            }

            innermost.Add(root);
        }

        ExploreVerdict? refusal = null;

        foreach (var owner in innermost)
        {
            if (children.Refusal(owner, owner.Naming(target)!) is not { } refused)
            {
                // One declaration recognises this child, which settles it: a child is recognised
                // however many other providers also own the directory holding it.
                return ExploreVerdict.Unclassified;
            }

            // The first refusal is the one the user is shown, because a reason naming the wrong
            // provider's rule is worse than either of them alone.
            refusal ??= refused;
        }

        // Null where no declaration covers this path, which is the ordinary case: most of a drive
        // belongs to no tool root at all.
        return refusal ?? ExploreVerdict.Unclassified;
    }

    /// <summary>
    /// The refusal of the deepest probed root that contains <paramref name="target"/> and refuses
    /// it, or null where none does.
    ///
    /// <para><b>Each probed root answers on its own</b>, never pooled with the others as the declared
    /// roots are in <see cref="Below"/>. Declared roots pool because several providers own one folder
    /// with disjoint lists of what may go, and none of them is complete alone. A probed root needs no
    /// other's permission, and two can land on one folder by accident: a vcpkg clone, and a binary
    /// cache a variable put inside it, both declare the clone. Pooled, the root that recognises every
    /// child lifted the clone's refusal of <c>installed</c>, and a root deeper inside a folder in use
    /// answered for everything below it.</para>
    ///
    /// <para><b>What is inside is refused with the root's own reason.</b> A probed root's reason is
    /// written about the whole folder — a program is using it, it holds the programs
    /// <c>go install</c> put there — while the sentence <see cref="ToolRootChildren.Refusal"/> gives
    /// an unrecognised child speaks of configuration beside a cache. Said of a file in a folder a program is working
    /// in, that sentence is untrue, and the user reading it is deciding whether to wait. A root that
    /// claims only <see cref="ToolRootClaim.NamedEntries"/> is the exception: the rest of its folder is
    /// someone else's, so its refusal names the entry rather than the folder.</para>
    /// </summary>
    private ExploreVerdict? ProbedRefusal(string target, ToolRootChildren children)
    {
        ExploreVerdict? refusal = null;
        int? levels = null;

        foreach (var root in _probedRoots)
        {
            if (root.Folder.LevelsTo(target) is not { } below
                || below >= levels
                || root.Naming(target) is not { } named
                || children.Refusal(root, named) is not { } refused)
            {
                continue;
            }

            var name = Path.GetFileName(named);

            refusal = below == 0
                ? refused
                : root.Root.Claim == ToolRootClaim.NamedEntries
                    ? ExploreVerdict.Refuse($"'{name}' is the tool's, in '{root.Path}': {root.Root.Reason}")
                    : ExploreVerdict.Refuse(
                        $"'{name}' is inside '{root.Path}', and Explore refuses what is in there "
                        + $"as well as '{Path.GetFileName(root.Path)}' itself: {root.Root.Reason}");
            levels = below;
        }

        return refusal;
    }
}
