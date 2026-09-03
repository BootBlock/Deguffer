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
/// The first is a table of regions — the operating system's own directories, the drive roots, the
/// signed-in user's profile — which is a fact about Windows and is stated here. The second is
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

        _regions =
        [
            .. regions
                .Select(r => r with { Path = LongPath.Configured(r.Path) ?? r.Path })
                .OrderByDescending(r => r.Path.Length)
                .ThenBy(r => r.Scope == RegionScope.PathOnly ? 0 : 1),
        ];

        _toolRoots = [.. toolRoots];
    }

    /// <summary>
    /// The policy for this machine: Windows' own directories, the mounted volumes, the signed-in
    /// user's profile, and every §5.2 declaration the providers make.
    ///
    /// <para>Assembled from the three seams rather than from <see cref="Environment"/> directly, so
    /// the whole of §7.1's refusal set is provable against a synthetic profile and synthetic drives
    /// — which is what G1's dependency inversion is for, and the only way these assertions can run
    /// on a machine where nobody may delete anything in <c>C:\Windows</c>.</para>
    /// </summary>
    public static ExploreActionPolicy For(
        ISystemDirectories system,
        IUserEnvironment environment,
        IVolumeInventory volumes,
        IEnumerable<ICleanupProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentNullException.ThrowIfNull(providers);

        return new ExploreActionPolicy(
            Regions(system, environment, volumes),
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

        // A whole volume, or something that has no containing directory. Neither is a thing to
        // remove, and the table below names the drive roots it was told about — this catches the
        // one it was not, such as a drive mounted after the page was opened.
        if (Path.GetDirectoryName(target) is null)
        {
            return ExploreVerdict.Refuse(
                $"'{target}' is a whole drive. Explore removes things from a drive, never the drive itself.");
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
    /// §5.2, asked of every tool root the providers declared, or the unclassified answer when none
    /// of them contains this path.
    /// </summary>
    private ExploreVerdict Below(string target)
    {
        foreach (var root in _toolRoots)
        {
            if (Refusal(root, target) is { } refusal)
            {
                return refusal;
            }
        }

        return ExploreVerdict.Unclassified;
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
    private static ExploreVerdict? Refusal(ToolRoot root, string target)
    {
        if (LongPath.Configured(root.Path) is not { } rootPath || !LongPath.Contains(rootPath, target))
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
        IUserEnvironment environment,
        IVolumeInventory volumes)
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

        foreach (var volume in volumes.Volumes)
        {
            foreach (var region in VolumeRegions(volume.RootPath))
            {
                yield return region;
            }
        }
    }

    /// <summary>
    /// What is refused on every mounted volume: the root itself, and the four things Windows keeps
    /// at a volume root that are not the user's to remove.
    ///
    /// <para>They are named rather than left to the operating system's own refusal because Explore
    /// draws them. <c>System Volume Information</c> and the paging files are among the largest
    /// items on a drive, so they are precisely what a size picture puts in front of somebody — and
    /// "access denied" from a deletion the app offered is a worse answer than not offering it.</para>
    /// </summary>
    private static IEnumerable<ProtectedRegion> VolumeRegions(string root)
    {
        yield return ProtectedRegion.Refusing(
            root,
            RegionScope.PathOnly,
            $"'{root}' is a whole drive. Explore removes things from a drive, never the drive itself.");

        yield return ProtectedRegion.Refusing(
            Path.Combine(root, "System Volume Information"),
            RegionScope.PathAndBelow,
            "Windows keeps this drive's restore points, indexing data and change journal here. It "
            + "belongs to the operating system, and Windows is what should reclaim it.");

        yield return ProtectedRegion.Refusing(
            Path.Combine(root, "$Recycle.Bin"),
            RegionScope.PathAndBelow,
            "This is the drive's Recycle Bin. Emptying it is offered on the Storage page, where "
            + "Deguffer can tell your own deleted files from another account's.");

        foreach (var (name, what) in new[]
        {
            ("pagefile.sys", "the paging file"),
            ("swapfile.sys", "the swap file"),
            ("hiberfil.sys", "the hibernation file"),
        })
        {
            yield return ProtectedRegion.Refusing(
                Path.Combine(root, name),
                RegionScope.PathAndBelow,
                $"This is {what}. Windows manages it, and its size is changed through the system "
                + "settings rather than by deleting it.");
        }
    }
}
