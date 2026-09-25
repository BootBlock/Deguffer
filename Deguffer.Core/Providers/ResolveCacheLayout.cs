using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

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
/// rebuilds. Only a folder holding a <c>.dvcc</c> file is recognised, because optimised media uses
/// the same extension and the folder name is the only thing that tells the two apart.</para>
/// </summary>
public static class ResolveCacheLayout
{
    /// <summary>The folder Resolve writes its render cache and optimised media into.</summary>
    public const string CacheFolderName = "CacheClip";

    /// <summary>The render cache's file extension.</summary>
    public const string RenderFileExtension = ".dvcc";

    /// <summary>The program's process name, as the process table reports it.</summary>
    public static readonly IReadOnlyList<string> ProcessNames = ["Resolve"];

    /// <summary>
    /// Children of <c>CacheClip</c> that are never render cache, whatever they hold, with what the
    /// user is told about each.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> NeverCache =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OptimizedMedia"] =
                "Resolve's optimised media. Resolve does not rebuild it on its own, and it is left alone.",
            ["ProxyMedia"] =
                "Resolve's proxy media. With the camera originals archived, a proxy is the only copy "
                + "that can be edited, so it is left alone.",
        };

    /// <summary>
    /// What Resolve writes beside <c>CacheClip</c>, with what the user is told about each. Asserted to
    /// survive and refused in Explore wherever it sits beside a recognised cache.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, string Reason)> Neighbours =
    [
        ("ProjectBackup", "Resolve's backups of your projects and timelines."),
        ("Capture", "Audio you recorded in Resolve, such as voiceover. Nothing can recreate it."),
        ("Resolve Live", "The snapshots Resolve Live took on set."),
        (".gallery", "Your saved stills and PowerGrades."),
        ("ProxyMedia",
            "Resolve's proxy media. With the camera originals archived, a proxy is the only copy that "
            + "can be edited."),
    ];

    public static bool IsCacheFolder(string name) =>
        name.Equals(CacheFolderName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The reason a named child of <c>CacheClip</c> is never render cache, or null.</summary>
    public static string? NeverCacheReason(string name) => NeverCache.GetValueOrDefault(name);

    /// <summary>
    /// Every folder Resolve may have created <c>CacheClip</c> in without being told where: the root
    /// of each local drive that holds its own content, and the Videos folder. A network share and a
    /// drive whose content is stored elsewhere are left out, because listing either is not a look at
    /// this computer's disk.
    /// </summary>
    public static IReadOnlyList<string> CandidateFolders(IVolumeInventory volumes, IUserEnvironment environment)
    {
        var folders = volumes.Volumes
            .Where(volume => volume is { IsReady: true, Kind: DriveType.Fixed or DriveType.Removable }
                && !volume.StoresContentRemotely)
            .Select(volume => volume.RootPath)
            .ToList();

        if (environment.Videos is { } videos)
        {
            folders.Add(videos);
        }

        return [.. folders.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Whether <paramref name="folder"/> holds a render file anywhere below it. Links are not followed,
    /// and a folder that will not be listed is skipped, so a refusal can only withhold recognition.
    /// </summary>
    public static bool HoldsRenderFiles(string folder, CancellationToken ct)
    {
        try
        {
            // Constructed inside the try, because the enumerator opens the folder as it is built.
            var renders = new System.IO.Enumeration.FileSystemEnumerable<bool>(
                LongPath.Extended(folder),
                static (ref System.IO.Enumeration.FileSystemEntry _) => true,
                RenderFileSearch)
            {
                ShouldIncludePredicate = static (ref System.IO.Enumeration.FileSystemEntry entry) =>
                    !entry.IsDirectory && entry.FileName.EndsWith(RenderFileExtension, StringComparison.OrdinalIgnoreCase),

                // A folder that is not a project's cache is walked to its end, so the walk stops going
                // deeper once the plan is cancelled rather than finishing first.
                ShouldRecursePredicate = (ref System.IO.Enumeration.FileSystemEntry _) => !ct.IsCancellationRequested,
            };

            if (renders.Any())
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Not recognised, which is the direction §5.2 requires an unanswered question to fall in.
        }

        ct.ThrowIfCancellationRequested();
        return false;
    }

    private static readonly EnumerationOptions RenderFileSearch = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };
}
