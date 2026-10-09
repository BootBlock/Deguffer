using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// §5.6 for a removal of items named one by one, on any page: the folder each item was taken out of
/// is still there, and everything that stood beside it still does.
///
/// <para><b>Listed before anything goes</b>, because a survivor set gathered afterwards can only
/// describe what is left, which would agree with any removal however over-broad. Names rather than
/// paths, and one listing per folder rather than one probe per sibling: a folder can hold two hundred
/// thousand entries, and the question (did anything <em>else</em> go?) is answered by comparing two
/// listings.</para>
///
/// <para><b>Every name is compared exactly</b>, never without regard to case. NTFS lets a folder be
/// case-sensitive, and there <c>a.txt</c> and <c>A.txt</c> are two files: a check that ignored case
/// would let the removed <c>a.txt</c> stand in for a lost <c>A.txt</c>, and would take two folders
/// whose names differ only in case for one (§7.4). So a removed item's path must name it as Windows
/// lists it, which the paths handed here do: a scan's names and a final path's are Windows' own.</para>
/// </summary>
public sealed class SiblingCheck
{
    private const string BesideReason = "Everything beside the removed item must survive.";

    private readonly Dictionary<string, IReadOnlyList<string>?> _before;
    private readonly IFileSystem _fs;

    private SiblingCheck(Dictionary<string, IReadOnlyList<string>?> before, IFileSystem fs)
    {
        _before = before;
        _fs = fs;
    }

    /// <summary>
    /// List every folder <paramref name="paths"/> will be taken out of, immediately before the
    /// removal. Blocks on each listing, so never call it on the UI thread.
    /// </summary>
    /// <param name="paths">Full paths in display form, normalised, with no trailing separator.</param>
    public static SiblingCheck Take(IEnumerable<string> paths, IFileSystem fs)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(fs);

        Dictionary<string, IReadOnlyList<string>?> before = new(StringComparer.Ordinal);

        foreach (var path in paths)
        {
            if (Path.GetDirectoryName(path) is { } parent && !before.ContainsKey(parent))
            {
                before[parent] = Names(parent, fs);
            }
        }

        return new SiblingCheck(before, fs);
    }

    /// <summary>
    /// Two checks per folder listed: that it is still there, and that everything beside the items in
    /// <paramref name="removed"/> is still in it, by its exact name.
    /// </summary>
    /// <param name="removed">The paths that went, as they were handed to <see cref="Take"/>.</param>
    public IReadOnlyList<VerificationCheck> Verify(IEnumerable<string> removed)
    {
        ArgumentNullException.ThrowIfNull(removed);

        // Keyed by the whole path rather than by the leaf name. Pooling names across folders excuses
        // a same-named sibling somewhere else: with 'projA\bin' removed, a removal that also took
        // 'projB\bin' would pass, and that is exactly the over-broad case this check exists for.
        var gone = removed.ToHashSet(StringComparer.Ordinal);
        var checks = new List<VerificationCheck>(_before.Count * 2);

        foreach (var (parent, names) in _before)
        {
            // No outcome here is ever VerificationOutcome.RemovedFromOutside: the listing is taken
            // immediately before the removal, so there is no preview on screen for the machine to
            // change under.
            checks.Add(Survival(
                parent,
                "The folder the item was taken out of must survive.",
                _fs.ProbeDirectory(LongPath.Extended(parent)),
                "MISSING — it was there before the removal."));

            // A listing that never happened is recorded as a failure rather than as a pass. §5.6 is
            // what turns "I think it worked" into evidence, and filing a non-assertion as evidence is
            // the one thing that undoes it.
            checks.Add(names is null
                ? new VerificationCheck(
                    parent,
                    BesideReason,
                    VerificationOutcome.Failed,
                    "NOT ESTABLISHED — this folder would not list its contents, so nothing beside "
                    + "the removed item could be checked.")
                : Beside(parent, names, gone));
        }

        return checks;
    }

    /// <summary>
    /// One path's survival. A path Windows will not describe afterwards is a check that could not be
    /// made, as <see cref="PlanVerifier"/> reports it: reading it as missing claims a deletion nobody
    /// saw, and reading it as a survivor claims what nobody saw either.
    /// </summary>
    internal static VerificationCheck Survival(string path, string reason, PathPresence after, string missing) =>
        after switch
        {
            PathPresence.Present => new VerificationCheck(path, reason, VerificationOutcome.Survived, "Still present."),
            PathPresence.Refused => new VerificationCheck(
                path,
                reason,
                VerificationOutcome.Unverified,
                "NOT CHECKED — it was there before the removal, and Windows would not describe it "
                + "afterwards, so nothing shows whether it survived."),
            _ => new VerificationCheck(path, reason, VerificationOutcome.Failed, missing),
        };

    private VerificationCheck Beside(string parent, IReadOnlyList<string> before, HashSet<string> gone)
    {
        if (Names(parent, _fs) is not { } after)
        {
            return new VerificationCheck(
                parent, BesideReason, VerificationOutcome.Failed,
                "Could not be checked: this folder listed its contents before the removal and would "
                + "not afterwards.");
        }

        bool WasRemoved(string name) => gone.Contains(Path.Combine(parent, name));

        var standing = after.ToHashSet(StringComparer.Ordinal);
        var missing = before.Where(n => !WasRemoved(n) && !standing.Contains(n)).ToList();
        var expected = before.Count(n => !WasRemoved(n));

        return missing.Count == 0
            ? new VerificationCheck(
                parent, BesideReason, VerificationOutcome.Survived,
                $"All {expected} other item(s) are still there.")
            : new VerificationCheck(
                parent, BesideReason, VerificationOutcome.Failed,
                $"MISSING — {missing.Count} of {expected} other item(s) went too, starting with "
                + $"'{string.Join("', '", missing.Take(3))}'.");
    }

    private static IReadOnlyList<string>? Names(string directory, IFileSystem fs)
    {
        try
        {
            return [.. fs.EnumerateEntries(LongPath.Extended(directory)).Select(e => Path.GetFileName(e.FullName))];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // Nothing rather than a partial view, on ChildDirectories.Under's reasoning: half a
            // listing would let a missing sibling read as one that was never there.
            return null;
        }
    }
}
