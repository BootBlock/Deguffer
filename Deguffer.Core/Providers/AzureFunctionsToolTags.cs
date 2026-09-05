using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// The Azure Functions tooling's own record of which downloaded release each Functions runtime line
/// currently uses.
///
/// <para>Beside <c>Releases</c> the tooling keeps <c>Tags\v1</c> … <c>Tags\v4</c>, and each of those
/// holds a <c>LastKnownGood-v&lt;sequence&gt;</c> file whose whole content is a release version —
/// <c>4.18.1</c>. That is the tooling saying, in its own words, which copy it will reach for. It is
/// worth reading because the alternative is Deguffer deciding for itself which release is current by
/// comparing version numbers, and §5.2's objection to that is exactly the substitution of our
/// knowledge of a tool's folder for the tool's own.</para>
///
/// <para>It changes nothing about what is offered. Every release is a download the tooling can
/// repeat, so every release is offered and none is pre-selected; this decides only what each row
/// <em>says</em>, which is the difference between "the release your v4 projects use" and "one
/// nothing points at any more".</para>
/// </summary>
internal static class AzureFunctionsToolTags
{
    /// <summary>The tag directory beside <c>Releases</c>, never a target.</summary>
    public const string DirectoryName = "Tags";

    /// <summary>The records inside one tag directory, one per feed sequence the tooling has seen.</summary>
    private const string RecordPattern = "LastKnownGood-*";

    /// <summary>
    /// A record names a version and nothing else, so anything longer is not one. It bounds what is
    /// read into memory from a directory Deguffer does not control.
    /// </summary>
    private const int LongestRecord = 64;

    /// <summary>
    /// Release version to the runtime lines whose records name it, or null when the records could
    /// not all be read.
    ///
    /// <para><b>All of them or none, and the strictness is the point.</b> What a row says about a
    /// release the map does not contain is "the tooling's own records no longer name this" — a claim
    /// about every record there is. Answering it from a partial reading states something nobody
    /// established: a machine has <c>Tags\v1</c> to <c>Tags\v4</c>, so one unreadable or junctioned
    /// tag beside three readable ones would leave its release, the one the developer's v2 projects
    /// actually use, described as abandoned and offered for deletion on that basis.</para>
    ///
    /// <para>So a tag that will not be read makes the whole answer null, and every row is then
    /// described neutrally. That silences a useful sentence in a rare configuration, which is the
    /// right way round: the alternative is an untrue sentence in the same configuration.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>>? Read(
        string root,
        CancellationToken ct = default)
    {
        var tags = Path.Combine(root, DirectoryName);

        if (!LongPath.DirectoryExists(tags))
        {
            return null;
        }

        var scan = ChildDirectories.Under(tags);

        // A link is never followed, on the rule every enumeration here follows: what it points at was
        // never classified. Together with a folder that refused to be listed, that is a tag whose
        // records were not read, so it takes the whole answer with it.
        if (scan.Unreadable || scan.Links.Count > 0)
        {
            return null;
        }

        var found = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in scan.Directories)
        {
            ct.ThrowIfCancellationRequested();

            if (VersionsNamedIn(tag, ct) is not { } versions)
            {
                return null;
            }

            foreach (var version in versions)
            {
                if (!found.TryGetValue(version, out var lines))
                {
                    found[version] = lines = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                lines.Add(tag.Name);
            }
        }

        if (found.Count == 0)
        {
            return null;
        }

        var named = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (version, lines) in found)
        {
            named[version] = [.. lines];
        }

        return named;
    }

    /// <summary>
    /// The versions the records in one tag directory name, or null where the directory would not be
    /// read. Null rather than an empty list, because the caller's answer is only worth anything if
    /// every tag was read — a tag that refused is not a tag that named nothing.
    /// </summary>
    private static IReadOnlyList<string>? VersionsNamedIn(DirectoryInfo tag, CancellationToken ct)
    {
        var versions = new List<string>();

        try
        {
            foreach (var record in tag.EnumerateFiles(RecordPattern))
            {
                ct.ThrowIfCancellationRequested();

                // An empty record names nothing, and one larger than a version string is not a
                // version string.
                if (record.Length is 0 or > LongestRecord)
                {
                    continue;
                }

                if (File.ReadAllText(record.FullName).Trim() is { Length: > 0 } version)
                {
                    versions.Add(version);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            return null;
        }

        return versions;
    }
}
