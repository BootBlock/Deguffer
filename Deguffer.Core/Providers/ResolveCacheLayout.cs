using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>What one child folder of <c>CacheClip</c> turned out to hold.</summary>
public enum RenderFolderReading
{
    /// <summary>Render files and nothing else, at every depth: one project's render cache.</summary>
    RenderCache,

    /// <summary>No render file at all, whatever else it holds.</summary>
    NoRenderFile,

    /// <summary>Render files beside a file of another kind or a link, so it is not wholly render cache.</summary>
    Mixed,

    /// <summary>Windows would not list all of it, so what it holds is not known.</summary>
    Unreadable,
}

/// <summary>
/// Where DaVinci Resolve writes its render cache on Windows, and the one kind of child of it that is
/// disposable.
///
/// <para><b>The cache has no fixed place.</b> Resolve's manual says it goes to a hidden
/// <c>CacheClip</c> folder created in the first Media Storage location, and to the system disk where
/// none is set. That location is whatever the user chose, and is routinely a drive's root. So the
/// cache is <em>found</em> where Resolve puts it by default, at the root of each local drive and in
/// the Videos folder one report names for the unset case, and never derived from Resolve's settings
/// or its project database, whose format is undocumented and changes between versions.</para>
///
/// <para><b>§5.2, and the neighbours are the user's only copies.</b> Beside <c>CacheClip</c> Resolve
/// writes <c>ProjectBackup</c>, <c>Capture</c> (recorded audio nothing regenerates),
/// <c>Resolve Live</c>, <c>.gallery</c> and <c>ProxyMedia</c>. None is ever a target. Inside
/// <c>CacheClip</c>, Resolve keeps one folder of <c>.dvcc</c> render files per project, and
/// <c>OptimizedMedia</c>, which its manual says must be deleted by hand and so is not a cache it
/// rebuilds. Optimised media uses the same extension, so its name is the only thing that tells it
/// apart, and a folder whose name begins with it is refused. Any other folder is recognised only
/// where everything in it, at every depth, is a render file: a folder is removed whole, so one stray
/// file of the user's would go with it.</para>
/// </summary>
public static class ResolveCacheLayout
{
    /// <summary>The folder Resolve writes its render cache and optimised media into.</summary>
    public const string CacheFolderName = "CacheClip";

    /// <summary>The render cache's file extension.</summary>
    public const string RenderFileExtension = ".dvcc";

    /// <summary>The program's process name, as the process table reports it.</summary>
    public static readonly IReadOnlyList<string> ProcessNames = ["Resolve"];

    private const string ProxyReason =
        "Resolve's proxy media. With the camera originals archived, a proxy is the only copy that can be edited.";

    /// <summary>
    /// Children of <c>CacheClip</c> that are never render cache, whatever they hold, by the start of
    /// their name, so a copy such as <c>OptimizedMedia_old</c> is refused as well, with what the user
    /// is told about each.
    /// </summary>
    private static readonly IReadOnlyList<(string Prefix, string Reason)> NeverCache =
    [
        ("OptimizedMedia", "Resolve's optimised media. Resolve does not rebuild it on its own, and it is left alone."),
        ("ProxyMedia", ProxyReason + " It is left alone."),
    ];

    /// <summary>
    /// What Resolve writes beside <c>CacheClip</c>, with what the user is told about each. Named as
    /// protected wherever it sits beside a <c>CacheClip</c>, so a run asserts it survived and Explore
    /// refuses it (§7.1).
    /// </summary>
    public static readonly IReadOnlyList<(string Name, string Reason)> Neighbours =
    [
        ("ProjectBackup", "Resolve's backups of your projects and timelines."),
        ("Capture", "Audio you recorded in Resolve, such as voiceover. Nothing can recreate it."),
        ("Resolve Live", "The snapshots Resolve Live took on set."),
        (".gallery", "Your saved stills and PowerGrades."),
        ("ProxyMedia", ProxyReason),
    ];

    /// <summary>The reason a child of <c>CacheClip</c> is never render cache, by its name, or null.</summary>
    public static string? NeverCacheReason(string name) =>
        NeverCache.FirstOrDefault(never => name.StartsWith(never.Prefix, StringComparison.OrdinalIgnoreCase)).Reason;

    /// <summary>
    /// Every folder Resolve may have created <c>CacheClip</c> in without being told where: the root
    /// of each local drive that holds its own content, and the Videos folder where it is on one of
    /// them. A network share and a drive whose content is stored elsewhere are left out: listing
    /// either is not a look at this computer's disk, and a Resolve on another computer may be writing
    /// there, which the process table here cannot see (§5.3).
    /// </summary>
    public static IReadOnlyList<string> CandidateFolders(IVolumeInventory volumes, IUserEnvironment environment)
    {
        var folders = volumes.Volumes
            .Where(IsLocalDisk)
            .Select(volume => volume.RootPath)
            .ToList();

        // A redirected Videos folder can be on a share, which no volume holds, or on a cloud mount.
        if (environment.Videos is { } videos && HostVolume.For(volumes, videos) is { } host && IsLocalDisk(host))
        {
            folders.Add(videos);
        }

        return [.. folders.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static bool IsLocalDisk(LocalVolume volume) =>
        volume is { IsReady: true, Kind: DriveType.Fixed or DriveType.Removable } && !volume.StoresContentRemotely;

    /// <summary>
    /// What <paramref name="folder"/> holds, at every depth. Links are not followed: one met anywhere
    /// makes the folder <see cref="RenderFolderReading.Mixed"/>, since what it points at was never
    /// classified. The walk stops as soon as it has met both a render file and anything else.
    /// </summary>
    public static RenderFolderReading Read(string folder, CancellationToken ct)
    {
        var sawRenderFile = false;
        var sawForeign = false;

        try
        {
            // Constructed inside the try, because the enumerator opens the folder as it is built.
            var entries = new System.IO.Enumeration.FileSystemEnumerable<EntryKind>(
                LongPath.Extended(folder),
                static (ref System.IO.Enumeration.FileSystemEntry entry) => Classify(ref entry),
                EveryEntry)
            {
                ShouldRecursePredicate = (ref System.IO.Enumeration.FileSystemEntry entry) =>
                    !ct.IsCancellationRequested && (entry.Attributes & FileAttributes.ReparsePoint) == 0,
            };

            foreach (var kind in entries)
            {
                sawRenderFile |= kind == EntryKind.RenderFile;
                sawForeign |= kind == EntryKind.Foreign;

                if (sawRenderFile && sawForeign)
                {
                    return RenderFolderReading.Mixed;
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // §5.3 makes a refusal ordinary. It is still not a folder known to hold only render files.
            return RenderFolderReading.Unreadable;
        }

        ct.ThrowIfCancellationRequested();
        return sawRenderFile ? RenderFolderReading.RenderCache : RenderFolderReading.NoRenderFile;
    }

    /// <summary>What one entry below a project folder is, for <see cref="Read"/>.</summary>
    private enum EntryKind
    {
        RenderFile,

        /// <summary>An ordinary folder, which decides nothing on its own.</summary>
        Folder,

        /// <summary>A link, or a file of another kind, either of which decides against the folder.</summary>
        Foreign,
    }

    private static EntryKind Classify(ref System.IO.Enumeration.FileSystemEntry entry) =>
        (entry.Attributes & FileAttributes.ReparsePoint) != 0 ? EntryKind.Foreign
        : entry.IsDirectory ? EntryKind.Folder
        : entry.FileName.EndsWith(RenderFileExtension, StringComparison.OrdinalIgnoreCase) ? EntryKind.RenderFile
        : EntryKind.Foreign;

    /// <summary>
    /// Hidden and system entries included, since <c>CacheClip</c> is itself hidden, and a refusal
    /// thrown rather than skipped, since a skipped folder is a partial view.
    /// </summary>
    private static readonly EnumerationOptions EveryEntry = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
    };
}
