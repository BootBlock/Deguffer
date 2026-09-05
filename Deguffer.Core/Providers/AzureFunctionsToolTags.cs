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
    /// Release version to the runtime lines whose records name it, or null when there are no
    /// records to read.
    ///
    /// <para>Null rather than an empty map, because the two mean opposite things to the sentence a
    /// row carries. "No record names this release" is worth saying and is what makes a superseded
    /// release identifiable; "there were no records" must not be reported as that, or every row on a
    /// machine whose <c>Tags</c> folder is missing or unreadable would claim the tooling had
    /// abandoned a release it uses daily.</para>
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

        var found = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

        // Links are separated out and not followed, on the rule every enumeration here follows: what
        // a link points at was never classified. A tag directory that is a link contributes nothing,
        // which leaves its release described neutrally rather than wrongly.
        foreach (var tag in ChildDirectories.Under(tags).Directories)
        {
            ct.ThrowIfCancellationRequested();

            foreach (var version in VersionsNamedIn(tag, ct))
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
    /// The versions the records in one tag directory name. Empty where the directory will not be
    /// read: one tag Deguffer may not open leaves the others readable, and a release with no record
    /// behind it is described neutrally. Nothing here decides what is deleted.
    /// </summary>
    private static IReadOnlyList<string> VersionsNamedIn(DirectoryInfo tag, CancellationToken ct)
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
            return versions;
        }

        return versions;
    }
}
