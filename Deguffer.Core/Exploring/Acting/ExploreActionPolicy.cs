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
/// The first is a table of regions — the operating system's own directories and the signed-in
/// user's profile — plus what Windows reserves at the top of any volume, which is read from the path
/// rather than from a list of drives. All of that is a fact about Windows and is stated here. The second is
/// §5.2, which is a fact about a tool and belongs to whichever provider knows the tool: Explore
/// reads it through <see cref="ToolRoot"/> rather than restating it, because a safety rule written
/// twice is one that gets changed once.</para>
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
    /// The names NTFS reserves in a volume's root directory, from <c>[MS-FSCC]</c>. See
    /// <see cref="ReservedByTheFilesystem"/> for why they are refused and why the set stops here.
    /// </summary>
    private static readonly HashSet<string> NtfsReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "$MFT", "$MFTMirr", "$LogFile", "$Volume", "$AttrDef", "$Bitmap",
        "$Boot", "$BadClus", "$Secure", "$UpCase", "$Extend",
    };

    private readonly IReadOnlyList<ProtectedRegion> _regions;
    private readonly IReadOnlyList<ToolRoot> _toolRoots;

    /// <param name="regions">
    /// The structural table. Sorted here rather than trusted from the caller, because the
    /// most-specific-wins rule is what makes an exception expressible and an unsorted table would
    /// resolve by declaration order instead — silently, and differently for each caller.
    /// </param>
    /// <param name="toolRoots">The §5.2 declarations, as the providers wrote them.</param>
    public ExploreActionPolicy(IEnumerable<ProtectedRegion> regions, IEnumerable<ToolRoot> toolRoots)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(toolRoots);

        // A region whose path will not resolve is dropped, not kept with the value it arrived
        // with. An empty one is the case that matters: LongPath.Contains("", candidate) builds the
        // prefix "\\" and so matches every UNC path, which would refuse a whole network share with a
        // sentence naming no directory at all. A path that names nothing protects nothing, and
        // %ProgramFiles(x86)% is genuinely empty on a 32-bit Windows.
        _regions =
        [
            .. regions
                .Select(r => (Region: r, Path: LongPath.Configured(r.Path)))
                .Where(r => r.Path is not null)
                .Select(r => r.Region with { Path = r.Path! })
                .OrderByDescending(r => r.Path.Length)
                .ThenBy(r => r.Scope == RegionScope.PathOnly ? 0 : 1),
        ];

        _toolRoots = [.. toolRoots];
    }

    /// <summary>
    /// The policy for this machine: Windows' own directories, the signed-in user's profile, and
    /// every §5.2 declaration the providers make. What sits at the top of a volume is decided from
    /// the path instead, so no list of drives has to be kept current.
    ///
    /// <para>Assembled from the two seams rather than from <see cref="Environment"/> directly, so
    /// the whole of §7.1's refusal set is provable against a synthetic profile — which is what G1's
    /// dependency inversion is for, and the only way these assertions can run on a machine where
    /// nobody may delete anything in <c>C:\Windows</c>.</para>
    /// </summary>
    public static ExploreActionPolicy For(
        ISystemDirectories system,
        IUserEnvironment environment,
        IEnumerable<ICleanupProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(providers);

        return new ExploreActionPolicy(
            Regions(system, environment),
            providers.SelectMany(p => p.ToolRoots));
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
        // Normalised first, because every comparison below is a prefix match on text. A path
        // carrying '..' compares equal to nothing and would walk straight past the whole table —
        // the same trap LongPath.Configured exists for on a provider's configured root.
        if (LongPath.Configured(path) is not { } target)
        {
            return ExploreVerdict.Refuse(
                "Deguffer could not make sense of that path, so it will not act on it.");
        }

        // A whole volume, or something with no containing directory at all. Neither is a thing to
        // remove, and asking the path rather than a list of drives means a volume mounted after this
        // policy was built is covered exactly as one mounted before it.
        if (Path.GetDirectoryName(target) is null)
        {
            return ExploreVerdict.Refuse(
                $"'{target}' is a whole drive. Explore removes things from a drive, never the drive itself.");
        }

        if (ReservedByTheFilesystem(target) is { } filesystem)
        {
            return filesystem;
        }

        if (AtAVolumeRoot(target) is { } reserved)
        {
            return reserved;
        }

        foreach (var region in _regions)
        {
            if (Covers(region, target))
            {
                return region.Verdict.IsAllowed ? Below(target) : region.Verdict;
            }
        }

        return Below(target);
    }

    /// <summary>
    /// What Windows keeps at the top of a volume, refused wherever the volume is.
    ///
    /// <para>Decided from the path rather than from a list of drives, and that is the point. A table
    /// built from <see cref="IVolumeInventory"/> is a snapshot: Explore re-reads its drive list
    /// whenever the page refreshes, so a volume mounted after the policy was built would be
    /// scannable with its paging file and its restore points unprotected. The question "is this a
    /// direct child of its own volume root, named one of these?" needs no inventory and is right on
    /// every drive, mounted before or after. <see cref="VolumeRoot"/> answers the first half of it,
    /// where <see cref="Knowledge.ItemGuide"/> can read the same rule rather than restate it.</para>
    ///
    /// <para>They are named at all because Explore draws them.
    /// <c>System Volume Information</c> and the paging files are among the largest items on a drive,
    /// so they are exactly what a size picture puts in front of somebody — and "access denied" from
    /// a deletion the app offered is a worse answer than not offering it.</para>
    /// </summary>
    private static ExploreVerdict? AtAVolumeRoot(string target)
    {
        if (!VolumeRoot.Holds(target))
        {
            return null;
        }

        return Path.GetFileName(target).ToLowerInvariant() switch
        {
            "system volume information" => ExploreVerdict.Refuse(
                "Windows keeps this drive's restore points, indexing data and change journal here. "
                + "It belongs to the operating system, and Windows is what should reclaim it."),

            "$recycle.bin" => ExploreVerdict.Refuse(
                "This is the drive's Recycle Bin. Emptying it is offered on the Storage page, where "
                + "Deguffer can tell your own deleted files from another account's."),

            "pagefile.sys" => Managed("the paging file"),
            "swapfile.sys" => Managed("the swap file"),
            "hiberfil.sys" => Managed("the hibernation file"),

            _ => null,
        };
    }

    /// <summary>
    /// NTFS's own records, which §7.1 puts out of reach: they are live filesystem state, so the tier
    /// model calls them Tier 4, and Explore "refuses whatever the tier model would call Tier 4, and
    /// it does not get to decide what that is".
    ///
    /// <para>Separate from <see cref="AtAVolumeRoot"/> and not folded into it, because the two ask
    /// different questions. That one is about a <em>direct child</em> of a volume root, which is
    /// where Windows keeps the paging file and the restore points. NTFS's optional features live a
    /// level down in <c>$Extend</c>, so this asks about the first segment below the root and covers
    /// everything under it.</para>
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
    private static ExploreVerdict? ReservedByTheFilesystem(string target) =>
        VolumeRoot.Below(target) is { } below
        && below.Split(Separators, StringSplitOptions.RemoveEmptyEntries) is [var first, ..]
        && NtfsReserved.Contains(first)
            ? ExploreVerdict.Refuse(
                $"'{first}' is part of NTFS itself rather than something stored on the drive — it is "
                + "how the filesystem records where every other file is. Windows does not let it be "
                + "deleted, and the space it holds is not recoverable while the drive is in use.")
            : null;

    private static ExploreVerdict Managed(string what) => ExploreVerdict.Refuse(
        $"This is {what}. Windows manages it, and its size is changed through the system settings "
        + "rather than by deleting it.");

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
    /// </summary>
    private ExploreVerdict Below(string target)
    {
        ToolRoot? innermost = null;
        var depth = -1;

        foreach (var root in _toolRoots)
        {
            if (LongPath.Configured(root.Path) is { } path
                && LongPath.Contains(path, target)
                && path.Length > depth)
            {
                innermost = root;
                depth = path.Length;
            }
        }

        return innermost is { } owner ? Refusal(owner, target) ?? ExploreVerdict.Unclassified
            : ExploreVerdict.Unclassified;
    }

    private static bool Covers(ProtectedRegion region, string target) =>
        region.Scope == RegionScope.PathAndBelow
            ? LongPath.Contains(region.Path, target)
            : target.Equals(region.Path, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// §5.2 for one tool root: the root is never a target, and below it the first segment decides.
    ///
    /// <para>The first segment and not the last, because that is the segment the provider
    /// classified. <c>.gradle\caches\modules-2</c> is inside a recognised child and goes with it,
    /// and <c>.gradle\init.d\anything</c> is inside an unrecognised one and does not — asking about
    /// the leaf instead would refuse the first and allow the second, which is exactly backwards.</para>
    /// </summary>
    /// <param name="root">
    /// The innermost root containing <paramref name="target"/>, already established by
    /// <see cref="Below"/> — so this re-resolves the path rather than re-checking containment.
    /// </param>
    private static ExploreVerdict? Refusal(ToolRoot root, string target)
    {
        if (LongPath.Configured(root.Path) is not { } rootPath)
        {
            return null;
        }

        if (target.Equals(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return ExploreVerdict.Refuse(root.Reason);
        }

        // Empty only if the remainder is separators alone, which Configured has already collapsed
        // into the equality above. Read as a refusal rather than indexed blindly: this is the one
        // predicate standing between a size picture and a tool's credentials.
        if (target[rootPath.Length..].Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            is not [var child, ..])
        {
            return ExploreVerdict.Refuse(root.Reason);
        }

        return root.Recognises(child)
            ? null
            : ExploreVerdict.Refuse(
                $"'{child}' is not something Deguffer recognises inside '{rootPath}'. Configuration "
                + "and credentials sit beside a cache in a tool's own folder, so anything unrecognised "
                + "there is left alone.");
    }

    /// <summary>
    /// The structural table. Every entry says what it protects and why, because the reason is what
    /// the user is shown.
    /// </summary>
    private static IEnumerable<ProtectedRegion> Regions(
        ISystemDirectories system,
        IUserEnvironment environment)
    {
        yield return ProtectedRegion.Refusing(
            system.WindowsDirectory,
            RegionScope.PathAndBelow,
            "This is inside the Windows directory. Deguffer never removes anything there from "
            + "Explore, and §9 of its specification excludes the component store and the installer "
            + "cache from every route, because a wrong removal there breaks uninstall or leaves the "
            + "machine unable to roll an update back.");

        foreach (var programs in new[] { system.ProgramFiles, system.ProgramFilesX86 })
        {
            yield return ProtectedRegion.Refusing(
                programs,
                RegionScope.PathAndBelow,
                $"This is installed software, under '{programs}'. Removing part of it leaves the "
                + "program on the machine and broken, and its own uninstaller is what should take it "
                + "away.");
        }

        yield return ProtectedRegion.Refusing(
            system.ProgramData,
            RegionScope.PathAndBelow,
            "This is machine-wide application data, shared by every account on this computer. "
            + "Deguffer has classified none of it, and the caches it does know about in there are "
            + "offered on the Storage page instead, where a provider knows what they are.");

        // The user's own profile, in three entries that read as one rule. The profile directory is
        // not a thing to remove and neither is the Users folder, but everything the user keeps
        // inside their own profile is ordinary — and another account's profile is not.
        var users = Path.GetDirectoryName(environment.UserProfile);

        if (users is not null)
        {
            yield return ProtectedRegion.Refusing(
                users,
                RegionScope.PathAndBelow,
                "This belongs to another account on this computer, or is the folder holding every "
                + "account's profile. Deguffer acts only inside the profile it is signed in to.");
        }

        yield return ProtectedRegion.Permitting(environment.UserProfile, RegionScope.PathAndBelow);

        yield return ProtectedRegion.Refusing(
            environment.UserProfile,
            RegionScope.PathOnly,
            "This is your whole profile — your documents, your settings and everything Deguffer "
            + "would otherwise offer to clean. Explore removes things from inside it, never the "
            + "profile itself.");
    }
}
