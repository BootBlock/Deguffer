using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What proves each cache inside a <c>.vs</c> folder is the one Visual Studio rebuilds, read from the
/// files inside it.
///
/// <para><b>No Microsoft page names these folders.</b> Microsoft documents the behaviour behind each
/// one instead: the design-time build cache, the project evaluation cache, the Find in Files index,
/// and the C++ browsing database and automatic precompiled headers. So a name is never the evidence
/// on its own. Each answer here is the shape observed across 42 <c>.vs</c> folders on one workstation,
/// and a folder of the right name holding anything else is left alone, which costs disk space rather
/// than work. An empty folder proves nothing and is never confirmed.</para>
/// </summary>
internal static partial class VisualStudioCacheEvidence
{
    /// <summary>
    /// <c>&lt;solution&gt;.projects</c>, <c>.metadata</c> and <c>.strings</c>, each with a format
    /// version. The version has had one part, two parts, and a <c>.bin</c> suffix in successive releases.
    /// </summary>
    private static readonly ContentSignature EvaluationFiles = new(EvaluationFile());

    private static readonly ContentSignature DesignTimeBuildFiles = new(DesignTimeBuildFile());

    /// <summary><c>ProjectEvaluation</c>: the evaluation files, and nothing else.</summary>
    public static bool ProjectEvaluation(string directory) => EvaluationFiles.Matches(directory);

    /// <summary><c>DesignTimeBuild</c>: <c>.dtbcache.v&lt;n&gt;</c>, and nothing else.</summary>
    public static bool DesignTimeBuild(string directory) => DesignTimeBuildFiles.Matches(directory);

    /// <summary>
    /// <c>FileContentIndex</c>: at least one <c>&lt;guid&gt;.vsidx</c>, an optional <c>read.lock</c>,
    /// and an optional <c>merges</c> folder holding only index files.
    /// </summary>
    public static bool FileContentIndex(string directory)
    {
        if (FolderEntries.Of(directory) is not { } entries)
        {
            return false;
        }

        var indexes = 0;

        foreach (var entry in entries)
        {
            if (IsLink(entry))
            {
                return false;
            }

            if (entry is FileInfo && IndexFile().IsMatch(entry.Name))
            {
                indexes++;
            }

            // Empty on every machine measured, so an empty one is allowed. It proves nothing on its
            // own, and the index files beside it are what the rule requires.
            else if (!(entry is DirectoryInfo && entry.Name.Equals("merges", StringComparison.OrdinalIgnoreCase) && HoldsOnly(entry.FullName, IndexFile()))
                && !(entry is FileInfo && entry.Name.Equals("read.lock", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return indexes > 0;
    }

    /// <summary>
    /// <c>ipch</c>: one <c>AutoPCH</c> folder of folders, each holding only <c>.ipch</c> files, with at
    /// least one <c>.ipch</c> among them.
    /// </summary>
    public static bool PrecompiledHeaders(string directory)
    {
        if (FolderEntries.Of(directory) is not [DirectoryInfo automatic] || IsLink(automatic)
            || !automatic.Name.Equals("AutoPCH", StringComparison.OrdinalIgnoreCase)
            || FolderEntries.Of(automatic.FullName) is not { } headers)
        {
            return false;
        }

        var compiled = 0;

        foreach (var header in headers)
        {
            if (header is not DirectoryInfo || IsLink(header) || !HoldsOnly(header.FullName, HeaderFile()))
            {
                return false;
            }

            compiled += FolderEntries.Of(header.FullName)?.Count ?? 0;
        }

        return compiled > 0;
    }

    /// <summary><c>Browse.VC.db</c> and <c>Solution.VC.db</c>: an SQLite database with no journal beside it.</summary>
    public static bool Database(string file) => SqliteDatabaseSignature.IsIdleDatabase(file);

    /// <summary>
    /// Whether <paramref name="directory"/> holds files alone, each matching <paramref name="permitted"/>,
    /// or nothing at all. <see cref="ContentSignature"/> is the same rule for a folder that must hold a
    /// file, and these two folders are empty as often as not.
    /// </summary>
    private static bool HoldsOnly(string directory, Regex permitted) =>
        FolderEntries.Of(directory) is { } entries
        && entries.All(entry => entry is FileInfo && !IsLink(entry) && permitted.IsMatch(entry.Name));

    private static bool IsLink(FileSystemInfo entry) => entry.Attributes.HasFlag(FileAttributes.ReparsePoint);

    [GeneratedRegex(@"^[^\/]+\.(?:projects|metadata|strings)\.v\d+(?:\.\d+)?(?:\.bin)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EvaluationFile();

    [GeneratedRegex(@"^\.dtbcache\.v\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DesignTimeBuildFile();

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.vsidx$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IndexFile();

    [GeneratedRegex(@"\.ipch$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeaderFile();
}
