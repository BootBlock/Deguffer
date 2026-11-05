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
    public static bool ProjectEvaluation(VisualStudioListing folder, VisualStudioEntry cache) =>
        Matches(EvaluationFiles, folder.Inside(cache));

    /// <summary><c>DesignTimeBuild</c>: <c>.dtbcache.v&lt;n&gt;</c>, and nothing else.</summary>
    public static bool DesignTimeBuild(VisualStudioListing folder, VisualStudioEntry cache) =>
        Matches(DesignTimeBuildFiles, folder.Inside(cache));

    /// <summary>
    /// <c>FileContentIndex</c>: at least one <c>&lt;guid&gt;.vsidx</c>, an optional <c>read.lock</c>,
    /// and an optional <c>merges</c> folder holding only index files.
    /// </summary>
    public static bool FileContentIndex(VisualStudioListing folder, VisualStudioEntry cache)
    {
        var index = folder.Inside(cache);

        if (index.Entries is not { } entries)
        {
            return false;
        }

        var indexes = 0;

        foreach (var entry in entries)
        {
            if (entry.IsLink)
            {
                return false;
            }

            if (entry.Kind == TargetKind.File && IndexFile().IsMatch(entry.Name))
            {
                indexes++;
            }

            // Empty on every machine measured, so an empty one is allowed. It proves nothing on its
            // own, and the index files beside it are what the rule requires.
            else if (!(entry.Kind == TargetKind.Directory && entry.Name.Equals("merges", StringComparison.OrdinalIgnoreCase) && HoldsOnly(index.Inside(entry), IndexFile()))
                && !(entry.Kind == TargetKind.File && entry.Name.Equals("read.lock", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return indexes > 0;
    }

    /// <summary>
    /// <c>ipch</c>: one <c>AutoPCH</c> folder of folders, each holding only <c>.ipch</c> files, with at
    /// least one <c>.ipch</c> among them. Each header folder is counted from the listing that proved
    /// what it holds.
    /// </summary>
    public static bool PrecompiledHeaders(VisualStudioListing folder, VisualStudioEntry cache)
    {
        var ipch = folder.Inside(cache);

        if (ipch.Entries is not [{ Kind: TargetKind.Directory, IsLink: false } automatic]
            || !automatic.Name.Equals("AutoPCH", StringComparison.OrdinalIgnoreCase)
            || ipch.Inside(automatic) is not { Entries: { } headers } listed)
        {
            return false;
        }

        var compiled = 0;

        foreach (var header in headers)
        {
            if (header.Kind != TargetKind.Directory || header.IsLink || listed.Inside(header) is not { Entries: { } files } inner
                || !HoldsOnly(inner, HeaderFile()))
            {
                return false;
            }

            compiled += files.Count;
        }

        return compiled > 0;
    }

    /// <summary><c>Browse.VC.db</c> and <c>Solution.VC.db</c>: an SQLite database with no journal beside it.</summary>
    public static bool Database(VisualStudioListing folder, VisualStudioEntry cache) =>
        SqliteDatabaseSignature.IsIdleDatabase(cache.Path, folder.Holds);

    private static bool Matches(ContentSignature signature, VisualStudioListing cache) =>
        cache.Entries is { } entries && signature.Matches(entries.Select(entry => (entry.Name, entry.Attributes)));

    /// <summary>
    /// Whether <paramref name="listing"/> holds files alone, each matching <paramref name="permitted"/>,
    /// or nothing at all. <see cref="ContentSignature"/> is the same rule for a folder that must hold a
    /// file, and these two folders are empty as often as not.
    /// </summary>
    private static bool HoldsOnly(VisualStudioListing listing, Regex permitted) =>
        listing.Entries is { } entries
        && entries.All(entry => entry.Kind == TargetKind.File && !entry.IsLink && permitted.IsMatch(entry.Name));

    [GeneratedRegex(@"^[^\/]+\.(?:projects|metadata|strings)\.v\d+(?:\.\d+)?(?:\.bin)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EvaluationFile();

    [GeneratedRegex(@"^\.dtbcache\.v\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DesignTimeBuildFile();

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.vsidx$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IndexFile();

    [GeneratedRegex(@"\.ipch$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeaderFile();
}
