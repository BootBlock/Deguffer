using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Where Maxon's Redshift renderer keeps its disk cache on this machine, and what beside it is never
/// touched.
///
/// <para><b>Measured from Cinebench 2026, which renders with Redshift inside a Cinema 4D shell.</b> Inside
/// a Maxon host, Redshift's local data folder is <c>Redshift</c> in the host's preferences folder,
/// <c>%APPDATA%\Maxon\&lt;host&gt;_&lt;id&gt;\Redshift</c>. Redshift's log names it as the local data path and
/// the folder <c>preferences.xml</c> is read from. The cache is <c>Cache</c> inside it, and holds
/// <c>Textures</c>, the textures Redshift converted to its own format as <c>.rstexbin</c> files in two
/// levels of numbered folders, and <c>Meshes</c>, which held one folder per computer and one per process
/// with a <c>lock</c> in it. One GPU benchmark run wrote 4.33 GB of converted textures.</para>
///
/// <para><b>The standalone default is Redshift's own.</b> <c>%LOCALAPPDATA%\Redshift\Cache</c> is the
/// default Maxon documents for a standalone install, and the one written into Redshift's core library and
/// into a new <c>preferences.xml</c>. No standalone install has been measured, so only the same
/// <c>Textures</c> folder is reached in it.</para>
///
/// <para><b>Only <c>Textures</c> is reached.</b> Maxon says missing cache files are "automatically
/// recalculated", and that "The caches must remain untouched only as long as a rendering of the
/// corresponding project is still running". <c>Meshes</c> holds per-process locks, so it is Tier 4. A
/// shader cache Redshift may keep beside them has not been seen, and anything else in <c>Cache</c> is never
/// enumerated, so it survives by construction.</para>
///
/// <para><b><c>REDSHIFT_CACHEPATH</c> moves the cache, and is followed.</b> Maxon documents it as the
/// variable that overrides the cache location, and Redshift keeps the same <c>Textures</c> folder inside
/// whatever it names. The folder is vetted by <see cref="ConfiguredFolder"/> like any folder a setting
/// names.</para>
///
/// <para><b>The <c>CacheFolder</c> preference is not read.</b> In the measured run, <c>preferences.xml</c>
/// named <c>%LOCALAPPDATA%\Redshift\Cache</c> while Redshift, by its own log, used the host's folder. A
/// setting that was measured disagreeing with the cache in use cannot be taken to say where the cache
/// is.</para>
///
/// <para><b>A cache on another computer's disk is withheld.</b> Redshift divides <c>Meshes</c> by
/// computer because a cache folder can be shared between machines, as a render farm shares one. A cache
/// that is not on a disk in this computer may be in use by a render elsewhere, which no process check
/// here can see.</para>
/// </summary>
/// <param name="Caches">The caches to examine, each reaching only its <c>Textures</c>.</param>
/// <param name="Notes">What the user is told about a cache that was found and withheld.</param>
/// <param name="LeftSomethingUnexamined">Whether a cache was named and withheld rather than examined.</param>
public sealed record RedshiftCacheLayout(
    IReadOnlyList<RedshiftCache> Caches,
    IReadOnlyList<PlanNote> Notes,
    bool LeftSomethingUnexamined)
{
    /// <summary>The variable Maxon documents as moving the cache.</summary>
    public const string CacheVariable = "REDSHIFT_CACHEPATH";

    /// <summary>Redshift's folder, in <c>%LOCALAPPDATA%</c> and in each Maxon host's preferences folder.</summary>
    public const string RedshiftFolder = "Redshift";

    /// <summary>The cache inside Redshift's folder.</summary>
    public const string CacheFolder = "Cache";

    /// <summary>The converted textures inside the cache, the one folder whose contents are removed.</summary>
    public const string TexturesFolder = "Textures";

    /// <summary>The mesh cache beside the textures, which holds a lock per running process.</summary>
    public const string MeshesFolder = "Meshes";

    /// <summary>Where Maxon's applications keep one preferences folder per installed application.</summary>
    public const string MaxonFolder = "Maxon";

    /// <summary>
    /// §5.3. The programs that load Redshift. <c>Cinebench</c> was measured rendering with it. The others
    /// are the hosts Maxon ships Redshift for, by the names of their programs: Cinema 4D and its
    /// command-line renderer, Maya, Houdini and its batch renderers, 3ds Max, Katana, Blender, and
    /// Redshift's own command-line renderer.
    /// </summary>
    public static readonly IReadOnlyList<string> ProcessNames =
    [
        "Cinebench", "Cinema 4D", "Commandline", "maya", "mayabatch", "houdini", "houdinifx", "houdinicore",
        "hython", "husk", "3dsmax", "katanaBin", "blender", "redshiftCmdLine",
    ];

    /// <summary>What the user is told about the converted textures, in the plan and in Explore.</summary>
    public const string TexturesReason =
        "Textures Redshift converted to its own format. Redshift converts them again from your images "
        + "the next time a scene that uses them renders.";

    private const string DataFolderReason =
        "Redshift's own folder. Its preferences, its logs and its mesh cache are in it, and only the "
        + "converted textures in its cache are removed.";

    private const string MovedReason =
        "The folder REDSHIFT_CACHEPATH names for Redshift's cache. Only the converted textures in it are "
        + "removed.";

    private const string MeshesReason =
        "Redshift's mesh cache, which holds a lock for each program rendering with it, so it is left alone.";

    private static readonly IReadOnlyList<(string RelativePath, string Reason)> DataFolderSurvivors =
    [
        ("preferences.xml", "Redshift's preferences: its cache budget and the devices it renders on."),
        ("Log", "Redshift's logs."),
        (Path.Combine(CacheFolder, MeshesFolder), MeshesReason),
    ];

    private static readonly IReadOnlyList<(string RelativePath, string Reason)> MovedSurvivors =
    [
        (MeshesFolder, MeshesReason),
    ];

    private static readonly DeclaredLocation DataFolderTextures = new(
        Path.Combine(CacheFolder, TexturesFolder), TexturesReason, DeclaredLocationKind.DirectoryContents, ReportsAge: false);

    private static readonly DeclaredLocation MovedTextures = new(
        TexturesFolder, TexturesReason, DeclaredLocationKind.DirectoryContents, ReportsAge: false);

    /// <summary>Redshift's folder for a standalone install.</summary>
    public static string StandaloneFolder(IUserEnvironment environment) =>
        Path.Combine(environment.LocalAppData, RedshiftFolder);

    /// <summary>The default cache of a standalone install, and the one <see cref="CacheVariable"/> moves.</summary>
    public static string DefaultCache(IUserEnvironment environment) =>
        Path.Combine(StandaloneFolder(environment), CacheFolder);

    /// <summary>The folder holding each Maxon application's preferences folder.</summary>
    public static string MaxonPreferences(IUserEnvironment environment) =>
        Path.Combine(environment.RoamingAppData, MaxonFolder);

    /// <summary>
    /// Every Redshift cache this account has, as roots that reach only <c>Textures</c>: the standalone
    /// folder, each Maxon host's, and the folder <see cref="CacheVariable"/> names.
    /// </summary>
    public static RedshiftCacheLayout Find(IUserEnvironment environment, ISystemDirectories machine, IVolumeInventory volumes)
    {
        var notes = new List<PlanNote>();
        var withheld = false;
        var roots = new List<(DeclaredRoot Root, string Folder, ReachedFolder Cache, ReachedFolder Textures)>();

        Add(StandaloneFolder(environment), CacheFolder, DataFolderReason, DataFolderTextures, DataFolderSurvivors);

        // A host's preferences folder is listed only to find its Redshift folder. Nothing in it is
        // classified, and a host without one is passed over. A host folder that is a link points at a
        // tree nothing here classified, so it is passed over too.
        var maxon = MaxonPreferences(environment);
        var hosts = ChildDirectories.Under(maxon);

        if (hosts.Unreadable)
        {
            withheld = true;
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"Windows would not list '{LongPath.Display(maxon)}', so a Redshift cache inside Cinema 4D or "
                + "another Maxon application was neither cleared nor ruled out."));
        }

        foreach (var host in hosts.Directories)
        {
            Add(Path.Combine(maxon, host.Name, RedshiftFolder), CacheFolder, DataFolderReason, DataFolderTextures, DataFolderSurvivors);
        }

        var setting = ConfiguredFolder.FromVariable(CacheVariable, DefaultCache(environment), environment, machine, volumes);

        if (setting.Declined is { } declined)
        {
            withheld = true;
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"{CacheVariable} names '{setting.Named ?? setting.Value}' for Redshift's cache, and Deguffer "
                + $"leaves it alone: {declined}"));
        }
        else if (setting.Folder is { } moved)
        {
            Add(moved, relativeCache: null, MovedReason, MovedTextures, MovedSurvivors);
        }

        // REDSHIFT_CACHEPATH can name a folder inside another cache's textures. Emptying those textures
        // takes the inner folder with it, so the inner one is left out: declared as well, its survivors
        // would be asserted to outlive a run that is bound to remove them.
        return new RedshiftCacheLayout(
            [
                .. roots
                    .Where(inner => !roots.Exists(outer => outer.Textures.Holds(inner.Cache)))
                    .Select(root => new RedshiftCache(root.Root, root.Folder)),
            ],
            notes,
            withheld);

        // One Redshift folder, examined once however it is named, and withheld where it is not on a disk
        // in this computer. Said only where something is there, so a machine without Redshift says nothing.
        void Add(string folder, string? relativeCache, string reason, DeclaredLocation textures, IReadOnlyList<(string, string)> survivors)
        {
            var cache = relativeCache is null ? folder : Path.Combine(folder, relativeCache);

            if (LongPath.ProbeDirectory(cache) is PathPresence.Absent)
            {
                return;
            }

            if (HostVolume.For(volumes, cache) is not { IsLocalDisk: true })
            {
                withheld = true;
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Left Redshift's cache in '{LongPath.Display(cache)}' alone: it is not on a disk in this "
                    + "computer, so a render on another computer may be using it."));

                return;
            }

            var reached = ReachedFolder.At(cache, volumes);

            if (roots.Exists(known => known.Cache.IsSameAs(reached)))
            {
                return;
            }

            roots.Add((
                new DeclaredRoot(folder, reason, RequiresElevation: false, [textures], survivors),
                cache,
                reached,
                ReachedFolder.At(Path.Combine(cache, TexturesFolder), volumes)));
        }
    }
}

/// <summary>One Redshift cache, and the folder it is declared under.</summary>
/// <param name="Declared">
/// Redshift's folder, with the cache inside it, or the cache itself where <c>REDSHIFT_CACHEPATH</c> named
/// it. Never a target.
/// </param>
/// <param name="Folder">The cache, which holds the textures and the mesh cache.</param>
public sealed record RedshiftCache(DeclaredRoot Declared, string Folder)
{
    /// <summary>The converted textures, the one folder whose contents are removed.</summary>
    public string Textures => Path.Combine(Folder, RedshiftCacheLayout.TexturesFolder);

    /// <summary>Whether <see cref="Declared"/> is Redshift's own folder around the cache, rather than the cache.</summary>
    public bool IsInsideDataFolder => !Declared.Path.Equals(Folder, StringComparison.OrdinalIgnoreCase);
}
