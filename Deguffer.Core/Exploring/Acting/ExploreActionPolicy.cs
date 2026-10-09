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
/// profile and Outlook's own folder — plus what Windows and NTFS reserve at the top of any volume,
/// which <see cref="TopOfVolumeRefusals"/> answers for and which is read from where the path's volume
/// is mounted rather than from a list of drives. Apart from Outlook's folder, all of that is a fact about
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
    /// <summary>
    /// How many providers <see cref="ForAsync"/> asks at once (G4). Not derived from the processor
    /// count, because what the handful of probing providers wait on is a subprocess or the process
    /// table rather than this machine's cores — and a user with every toolchain installed should not
    /// see ten console windows' worth of work start at the same instant.
    /// </summary>
    private const int Discovery = 8;

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
        if (TopOfVolumeRefusals.Refusal(places) is { } top)
        {
            return top;
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
    /// The refusal that holds for <paramref name="path"/> and for everything in it, or null where
    /// something in it, or the path itself, may be removed. A file is asked about as itself.
    ///
    /// <para><b>For a caller that goes through what a folder holds</b>, as a duplicate search does
    /// (§7.4): a place refused here can be passed over whole, and nothing in it asked about.
    /// <see cref="MayRemove"/> cannot serve, because it refuses a folder for what the folder
    /// <em>holds</em> as well (<see cref="HeldLocations"/>), so it refuses the Users folder, which
    /// holds the signed-in profile, and every folder above <c>C:\Windows</c>.</para>
    ///
    /// <para><b>The rules <see cref="MayRemove"/> asks first, through the same members</b>, so the two
    /// cannot come to disagree: what Windows and NTFS keep at the top of a volume, every Recycle Bin,
    /// Outlook's mail stores and the folder it saves them in, and each region of the table that covers what is
    /// below it, less what a permitting region inside the path carves back out. A path refused here
    /// is refused by <see cref="MayRemove"/> too.</para>
    ///
    /// <para>A whole volume is answered null rather than refused: it is never removed, and what is on
    /// it is asked about thing by thing.</para>
    /// </summary>
    public ExploreVerdict? RefusedAtAndBelow(string path)
    {
        if (LongPath.Configured(path) is not { } target)
        {
            return ExploreVerdict.Refuse(
                "Deguffer could not make sense of that path, so it will not act on it.");
        }

        if (VolumeRoot.Places(_volumes, target) is not { } places)
        {
            return null;
        }

        if (TopOfVolumeRefusals.Refusal(places) is { } top)
        {
            return top;
        }

        foreach (var place in ReachedFolder.Following(target, places).Places)
        {
            if (WithEverythingIn(place) is { } refusal)
            {
                return refusal;
            }
        }

        return null;
    }

    /// <summary>
    /// Where, below <paramref name="root"/>, <see cref="RefusedAtAndBelow"/> can answer differently
    /// for a child than for the folder holding it, so a caller going through millions of folders asks
    /// it about a handful rather than about each. See <see cref="RefusalWatch"/>.
    /// </summary>
    /// <param name="root">The folder the caller starts from, which it asks about itself.</param>
    public RefusalWatch WatchBelow(string root) =>
        new(ReachedFolder.At(root, _volumes).IsVolumeTop, [.. _regions.BoundaryPlaces]);

    /// <summary>
    /// The refusal of one place the item is reachable at that holds for everything in it too: Outlook's
    /// mail stores and the folder it saves them in, and the region table's rule for a whole folder. A
    /// mail store is a file, so it is all there is of it; a folder named like one is refused with what
    /// it holds, which errs, as the rule does, on the side of the mail.
    /// </summary>
    /// <param name="place">One path the item is reachable at, in <see cref="ReachedFolder.Comparable"/> form.</param>
    private ExploreVerdict? WithEverythingIn(string place) =>
        OutlookDataFiles.Refusal(place) ?? _regions.RefusingAtAndBelow(place);

    /// <summary>
    /// What everything that answers from above says about one place the item is reachable at: the
    /// Outlook rules, the region table, and §5.2's declared and probed roots, in that order.
    /// </summary>
    /// <param name="target">One path the item is reachable at, in <see cref="ReachedFolder.Comparable"/> form.</param>
    private ExploreVerdict Above(string target, ToolRootChildren children)
    {
        // The Outlook rules before the region table, because the table ends in a permission: a mail
        // store inside the signed-in profile would otherwise be answered by the profile's own entry
        // and allowed. These are the rules RefusedAtAndBelow asks of each place.
        if (WithEverythingIn(target) is { } refused)
        {
            return refused;
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
