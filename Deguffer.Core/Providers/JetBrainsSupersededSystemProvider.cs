using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The system directories of IntelliJ-platform IDE versions that have been replaced: IntelliJ IDEA,
/// Rider, PyCharm and the rest under <c>%LOCALAPPDATA%\JetBrains</c>, and Android Studio under
/// <c>%LOCALAPPDATA%\Google</c>.
///
/// <para><b>Offers part of what the IDE deletes by itself, under a stricter test.</b> After an
/// update, the IDE schedules a scan for seven days later, and on a project opened after that deletes
/// every older version's directories whose newest entry is more than 180 days old
/// (<c>OLD_DIRECTORIES_SHELF_LIFE_DAYS</c> in
/// <see href="https://github.com/JetBrains/intellij-community/blob/master/platform/platform-impl/update-checker/src/com/intellij/openapi/updateSettings/impl/UpdateCheckerProjectActivity.kt">UpdateCheckerProjectActivity.kt</see>).
/// For that automatic clean it deletes the system directory, and the logs directory only where logs
/// live outside it, never the configuration or the plugins (<c>getRelatedDirectories</c> with
/// <c>forAutoClean</c> in
/// <see href="https://github.com/JetBrains/intellij-community/blob/master/platform/platform-impl/initial-config-import/src/com/intellij/openapi/application/ConfigImportHelper.java">ConfigImportHelper.java</see>).
/// On Windows the platform keeps logs in the system directory's own <c>log</c> folder, so the system
/// directory is the whole of it. The IDE's automatic clean decides by age alone. This also requires
/// that the version is not the installed one by the IDE's own test: the <c>.home</c> file in the
/// system directory names the folder that version last started from, and that folder's
/// <c>product-info.json</c> names the data directory of whatever is installed there now
/// (<see href="https://github.com/JetBrains/intellij-community/blob/master/platform/platform-impl/initial-config-import/src/com/intellij/ide/OldDirectoryCleaner.java">OldDirectoryCleaner.java</see>).
/// Android Studio keeps the same layout under Google's name
/// (<see href="https://developer.android.com/studio/troubleshoot#directories">directories</see>).</para>
///
/// <para><b>Never configuration.</b> The configuration directory of the same name under
/// <c>%APPDATA%</c> holds settings, keymaps, plugins, and on a measured machine a credential database
/// (<c>c.kdbx</c>), its key (<c>c.pwd</c>) and a licence key. It is named as a survivor of every step.</para>
///
/// <para><b>Recognised children only (§5.2), and the Tier 3 ones never.</b> The IDE deletes the whole
/// system directory, but that directory also holds <c>LocalHistory</c>, the version's record of
/// earlier states of the user's files, and <c>log</c>, and §3 puts histories and logs in Tier 3
/// whoever else deletes them. So the version folder is never a target. What is offered is each child
/// the platform's own source names as derived data: <c>caches</c>, the virtual file system's store
/// (<c>getCacheDir</c> in
/// <see href="https://github.com/JetBrains/intellij-community/blob/master/platform/vfs-impl/src/com/intellij/openapi/vfs/newvfs/persistent/FSRecords.java">FSRecords.java</see>),
/// and <c>index</c> and <c>tmp</c>, the indices and temporary files (<c>getIndexRoot</c> and
/// <c>getTempDir</c> in
/// <see href="https://github.com/JetBrains/intellij-community/blob/master/platform/util/src/com/intellij/openapi/application/PathManager.java">PathManager.java</see>).
/// Every other child is unrecognised and stays. <c>plugins</c> here is not the plugins: it is the
/// folder of the start-up action script (<c>getStartupScriptDir</c>), and stays too.</para>
///
/// <para><b>Tier 1</b>, because those three are derived from the user's projects by a version that
/// is no longer installed, and the vendor deletes them without asking. There is no command to run:
/// the vendor's action is inside the IDE.</para>
/// </summary>
public sealed class JetBrainsSupersededSystemProvider : CleanupProviderBase
{
    /// <summary>The IDE's own shelf life for an old version's directories.</summary>
    public static readonly TimeSpan ShelfLife = TimeSpan.FromDays(180);

    /// <summary>The children of a version's system folder that are offered. See the class summary.</summary>
    public static readonly IReadOnlyList<string> DerivedChildren = ["caches", "index", "tmp"];

    /// <summary>The children that hold Tier 3 data, named as survivors of every step.</summary>
    private static readonly IReadOnlyList<string> UserDataChildren = ["LocalHistory", "log"];

    private readonly IReadOnlyList<VendorFolder> _vendors;
    private readonly TimeProvider _time;
    private readonly ScanTuner _tuning;
    private readonly JetBrainsLaunchCheck _launches;
    private readonly PassMemo<Survey> _survey = new();

    public JetBrainsSupersededSystemProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        TimeProvider? time = null,
        ScanTuner? tuning = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner,
            time: time)
    {
        _time = time ?? TimeProvider.System;
        _tuning = tuning ?? ScanTuner.Shipped;
        _launches = new JetBrainsLaunchCheck(_time, ShelfLife);

        // JetBrains' folder holds only its own products, each recognised by the platform's directory
        // name and then by the installed test. Google's holds Chrome, Drive and more, so only Android
        // Studio's names are read there.
        _vendors =
        [
            new VendorFolder(Path.Combine(Environment.LocalAppData, "JetBrains"), Path.Combine(Environment.RoamingAppData, "JetBrains"), Product: null),
            new VendorFolder(Path.Combine(Environment.LocalAppData, "Google"), Path.Combine(Environment.RoamingAppData, "Google"), Product: "AndroidStudio"),
        ];
    }

    public override string Id => "jetbrains-superseded-system";

    public override string Name => "Superseded JetBrains IDE caches";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    /// <summary>One step per version, and a version is something the user knows by name.</summary>
    public override StepGrain Grain => StepGrain.Items;

    public override string WhatHappensOnNextUse =>
        "Nothing, for the IDE you use: only versions it has replaced are offered, and only once " +
        "nothing in them has changed for 180 days, which is when the IDE deletes them itself. If you " +
        "start one of those old versions again, it builds its caches and indexes again from scratch. " +
        "Its local history and logs, its settings and its plugins are not touched.";

    public override ProviderBasis Basis => ProviderBasis.Observed;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.DeveloperTools,
        Application = "IntelliJ IDEA, Rider, PyCharm, Android Studio and the other IDEs built on the IntelliJ platform",
        Publisher = "JetBrains, and Google for Android Studio",
        Purpose = "Each IDE version keeps a system folder of caches, indexes and logs. An update "
            + "starts a new folder for the new version and leaves the old one behind.",
        Recommendation = "The IDE deletes an old version's system folder itself, once nothing in it "
            + "has changed for 180 days, but only on a project opened a week or more after an "
            + "update. Deguffer offers the caches, indexes and temporary files in those folders, "
            + "and only where the version is no longer the one installed. Local history, logs, "
            + "settings, plugins and licences are never offered.",
    };

    public override void InvalidateCaches()
    {
        base.InvalidateCaches();
        _survey.Forget();
    }

    /// <summary>
    /// Each version folder this row reads, so that Explore refuses in it what the plan leaves (§7.1).
    /// Only the derived children of a version the plan offers are recognised; everything else in any
    /// version folder, its local history and logs included, is the IDE's and stays. The vendor folders
    /// are not roots: Google's holds Chrome and Drive, which are not this row's to claim.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        .. Look(CancellationToken.None).Versions
            .Where(version => version.Verdict is not VersionVerdict.Link)
            .Select(version => ToolRoot.Folders(
                version.Directory.Path,
                "This is one IDE version's own folder. Deguffer removes only the caches, indexes and "
                + "temporary files of a version that is no longer installed and unused for 180 days.",
                version.Verdict is VersionVerdict.Offered
                    ? name => DerivedChildren.Contains(name, StringComparer.OrdinalIgnoreCase)
                    : _ => false)),
    ];

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(_vendors.Any(vendor => LongPath.DirectoryMayExist(vendor.Folder)));

    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>(
        [
            .. Look(ct).Versions.SelectMany(version => DerivedChildren.Select(child =>
                CleanedPlace.Whole(Path.Combine(version.Directory.Path, child)))),
        ]);

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var survey = Look(ct);

        if (survey.UnreadableFolders.Count == 0 && !survey.AnyFolder)
        {
            return EmptyPlan("No JetBrains IDE or Android Studio keeps a system folder for this user.");
        }

        var offered = survey.Versions.Where(v => v.Verdict is VersionVerdict.Offered).ToList();

        var (steps, measured) = await PlanDeletionsAsync(
            [
                .. offered.SelectMany(version => OfferedChildren(version.Directory.Path).Select(child => new DeletionTarget(
                    child,
                    $"{Path.GetFileName(child)} of {Path.GetFileName(version.Directory.Path)}, {WhyNotInstalled(version.Directory.Standing)}",
                    version.NewestWrite,
                    Facets: [new ItemFacet("Product", version.Directory.Product)],
                    Group: Path.GetFileName(version.Directory.Path),
                    UseCheck: _launches))),
            ],
            keep,
            ct).ConfigureAwait(false);

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            ProtectedPaths = BuildProtectedPaths(survey),
            Notes = JetBrainsPlanNotes.For(survey.Versions.Select(v => v.Verdict), survey.UnreadableFolders, measured.Note),
            Fallback = measured.Fallback,
            HasUnreadableRoot = survey.UnreadableFolders.Count > 0,
        };
    }

    /// <summary>
    /// The derived children of <paramref name="version"/> that are there as ordinary folders. A link
    /// is left alone, as everywhere: what it points at is somewhere else's.
    /// </summary>
    private static IEnumerable<string> OfferedChildren(string version) =>
        DerivedChildren
            .Select(child => Path.Combine(version, child))
            .Where(child => LongPath.ProbeDirectory(child) is PathPresence.Present && !LongPath.IsReparsePoint(child));

    /// <summary>
    /// §5.6. Each vendor folder, every child of it that is not offered, the folder of every version
    /// that is, with its local history and logs, and its configuration folder, by name: a rule
    /// reaching from <c>%LOCALAPPDATA%</c> into <c>%APPDATA%</c> would take the one folder here holding
    /// a credential store.
    /// </summary>
    private IReadOnlyList<ProtectedPath> BuildProtectedPaths(Survey survey) => Protect(
    [
        .. _vendors.Select(vendor => (vendor.Folder, "The vendor's own folder. Only replaced versions' system folders inside it are removed.")),
        .. survey.Versions.Where(v => v.Verdict is VersionVerdict.Offered).SelectMany(version => new[]
        {
            (Path.Combine(version.Vendor.ConfigurationFolder, Path.GetFileName(version.Directory.Path)),
             "This version's settings, plugins and stored credentials. The IDE never deletes them, and neither does Deguffer."),
            (version.Directory.Path, "The version's own folder. Only its caches, indexes and temporary files are removed."),
        }.Concat(UserDataChildren.Select(child => (
            Path.Combine(version.Directory.Path, child),
            "This version's local history of your files, or its logs. Lost for good if removed, so never offered here.")))),
        .. survey.Versions.Where(v => v.Verdict is not VersionVerdict.Offered).Select(version => (version.Directory.Path, KeptBecause(version.Verdict))),
        .. survey.Others.Select(other => (other, "Not an IDE version's system folder, so it is left alone.")),
    ]);

    private static string WhyNotInstalled(JetBrainsInstallStanding standing) => standing switch
    {
        JetBrainsInstallStanding.Superseded => "a version its install has since been updated past",
        JetBrainsInstallStanding.Uninstalled => "a version whose install folder is gone",
        _ => throw new ArgumentOutOfRangeException(nameof(standing), standing, null),
    };

    private static string KeptBecause(VersionVerdict verdict) => verdict switch
    {
        VersionVerdict.Installed => "The version installed now.",
        VersionVerdict.RecentlyUsed => "Something in it changed within the last 180 days.",
        VersionVerdict.AgeUnknown => "Part of it could not be read, so how recently it was used is not known.",
        VersionVerdict.StandingUnknown => "Whether this version is still installed could not be read.",
        VersionVerdict.Link => "It is a link to somewhere else, and Deguffer does not look through a link.",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, null),
    };

    private Survey Look(CancellationToken ct) => _survey.Get(() => Examine(ct));

    /// <summary>
    /// Every version folder in each vendor folder, and its verdict. The cheap tests come first, so a
    /// tree is walked for its age only for a version the installed test has already found replaced,
    /// and once a pass (G4). The size of what is offered is the scanner's own reading, as for every
    /// row, and covers only the derived children.
    /// </summary>
    private Survey Examine(CancellationToken ct)
    {
        var versions = new List<ExaminedVersion>();
        var others = new List<string>();
        var unreadable = new List<string>();
        var anyFolder = false;
        var cutoff = _time.GetUtcNow().UtcDateTime - ShelfLife;

        foreach (var vendor in _vendors)
        {
            ct.ThrowIfCancellationRequested();

            switch (LongPath.ProbeDirectory(vendor.Folder))
            {
                case PathPresence.Absent:
                    continue;
                case PathPresence.Refused:
                    unreadable.Add(vendor.Folder);
                    continue;
            }

            anyFolder = true;

            if (Children(vendor.Folder) is not { } children)
            {
                unreadable.Add(vendor.Folder);
                continue;
            }

            foreach (var (child, isLink) in children)
            {
                ct.ThrowIfCancellationRequested();

                if (JetBrainsSystemDirectory.ProductOf(child, vendor.Product) is not { } product)
                {
                    others.Add(child);
                    continue;
                }

                // Decided by the name and the link alone, before anything inside is read: a link's
                // .home is somewhere else's file, and so is the install it names.
                if (isLink)
                {
                    versions.Add(new ExaminedVersion(
                        vendor, new JetBrainsSystemDirectory(child, product, JetBrainsInstallStanding.Unknown), VersionVerdict.Link, null));
                    continue;
                }

                var directory = JetBrainsSystemDirectory.Read(child, product);

                versions.Add(directory.Standing switch
                {
                    JetBrainsInstallStanding.Installed => new ExaminedVersion(vendor, directory, VersionVerdict.Installed, null),
                    JetBrainsInstallStanding.Unknown => new ExaminedVersion(vendor, directory, VersionVerdict.StandingUnknown, null),
                    _ => Aged(vendor, directory, cutoff, ct),
                });
            }
        }

        return new Survey(versions, others, unreadable, anyFolder);
    }

    private ExaminedVersion Aged(VendorFolder vendor, JetBrainsSystemDirectory directory, DateTime cutoff, CancellationToken ct) =>
        TreeAge.NewestWrite(directory.Path, _tuning, ct) switch
        {
            null => new ExaminedVersion(vendor, directory, VersionVerdict.AgeUnknown, null),
            { } newest when newest > cutoff => new ExaminedVersion(vendor, directory, VersionVerdict.RecentlyUsed, newest),
            { } newest => new ExaminedVersion(vendor, directory, VersionVerdict.Offered, newest),
        };

    /// <summary>
    /// The folders directly inside <paramref name="folder"/>, each with whether it is a link, or null
    /// where the folder could not be listed.
    /// </summary>
    private static List<(string Path, bool IsLink)>? Children(string folder)
    {
        try
        {
            return
            [
                .. new DirectoryInfo(LongPath.Extended(folder))
                    .EnumerateDirectories()
                    .Select(child => (Path.Combine(folder, child.Name), child.Attributes.HasFlag(FileAttributes.ReparsePoint))),
            ];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Refused, or gone since it was probed. Either way nothing in it was seen.
            return null;
        }
    }

    /// <param name="Folder">Where the vendor's IDEs keep their system folders.</param>
    /// <param name="ConfigurationFolder">Where they keep configuration, under the same names.</param>
    /// <param name="Product">The only product read here, or null for any of the vendor's.</param>
    private sealed record VendorFolder(string Folder, string ConfigurationFolder, string? Product);

    private sealed record ExaminedVersion(VendorFolder Vendor, JetBrainsSystemDirectory Directory, VersionVerdict Verdict, DateTime? NewestWrite);

    private sealed record Survey(
        IReadOnlyList<ExaminedVersion> Versions,
        IReadOnlyList<string> Others,
        IReadOnlyList<string> UnreadableFolders,
        bool AnyFolder);
}

/// <summary>What the provider decided about one version's system folder.</summary>
internal enum VersionVerdict
{
    Offered,
    Installed,
    RecentlyUsed,
    AgeUnknown,
    StandingUnknown,
    Link,
}
