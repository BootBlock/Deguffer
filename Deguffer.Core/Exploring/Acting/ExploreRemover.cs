using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Exploring.Acting;

/// <summary>Which of §7.1's two removals the user asked for.</summary>
public enum ExploreRemovalMode
{
    /// <summary>
    /// The default. §7.1: where recovery is available it is not optional, and for the one file a
    /// user picked out of a picture it is available.
    /// </summary>
    RecycleBin,

    /// <summary>
    /// §7.1's "deliberate second choice that says what it is". Nothing is recoverable afterwards,
    /// which is the whole reason it is not the default.
    /// </summary>
    Permanent,
}

/// <param name="Path">The item, in display form.</param>
/// <param name="Bytes">
/// What the scan measured it at, carried forward rather than re-measured (G5). It may be a lower
/// bound — §7.1 allows Explore's numbers to be short, provided the picture says so — which is why
/// the report words a total as what was removed rather than as what was reclaimed.
/// </param>
public sealed record ExploreItem(string Path, bool IsDirectory, long Bytes);

/// <param name="Message">
/// What happened, written for the user. On a refusal it is the policy's own sentence, because §7.1
/// requires a refusal to state its reason rather than to grey something out.
/// </param>
public sealed record ExploreItemOutcome(string Path, bool Removed, long Bytes, string Message);

/// <summary>What one Explore removal did, and the §5.6 evidence that it did no more.</summary>
/// <param name="Cancelled">
/// Whether the run was stopped before it reached every item it was handed. The report still carries
/// what happened and what was verified up to that point, because §5.6's assertion is most needed
/// exactly when a removal ended somewhere nobody chose.
/// </param>
public sealed record ExploreRemovalReport(
    ExploreRemovalMode Mode,
    IReadOnlyList<ExploreItemOutcome> Items,
    VerificationResult Verification,
    bool Cancelled = false)
{
    public IReadOnlyList<ExploreItemOutcome> Removed => [.. Items.Where(i => i.Removed)];

    public IReadOnlyList<ExploreItemOutcome> Refused => [.. Items.Where(i => !i.Removed)];

    /// <summary>
    /// What the removed items were measured at.
    ///
    /// <para>Named for what it is rather than as "reclaimed", because for
    /// <see cref="ExploreRemovalMode.RecycleBin"/> it is not reclaimed at all: the bytes moved to
    /// the bin and the drive has the same free space it had. Saying otherwise is the specific
    /// untruth §8's fourth question is about, and it is the sentence a user would act on.</para>
    /// </summary>
    public long BytesRemoved => Removed.Sum(i => i.Bytes);

    /// <summary>
    /// What happened, in one sentence for the status line, with the §5.6 result attached whenever it
    /// did not pass.
    ///
    /// <para>Here rather than in the shell for the reason <see cref="VerificationResult.Summary"/>
    /// is: a failed negative assertion means a removal took something it was not asked to take, and
    /// whether the user is told that must not depend on which surface is rendering the report.</para>
    ///
    /// <para>A single refusal is quoted rather than counted. It is the ordinary case — the user
    /// picked one thing — and §7.1 requires the reason to be stated, which a count is not.</para>
    /// </summary>
    public string Summary
    {
        get
        {
            var sentence = (Removed.Count, Refused.Count) switch
            {
                (0, 1) => Refused[0].Message,
                (0, var refused) => $"Nothing was removed. {refused} items were refused.",
                (_, 0) => Did(),
                (_, 1) => $"{Did()} {Refused[0].Message}",
                (_, var refused) => $"{Did()} {refused} items were refused.",
            };

            if (Cancelled)
            {
                sentence = $"Stopped part-way. {sentence}";
            }

            // VerificationResult.Summary is deliberately not used: its wording is PlanVerifier's,
            // and it would describe a folder that could not be listed as one whose contents "did
            // not survive", which is a different and much stronger claim.
            return Verification.Passed
                ? sentence
                : $"{sentence} {Verification.Failures.Count} of {Verification.Checks.Count} "
                  + "check(s) on what should have survived did not pass. Look at the folder before "
                  + "doing anything else.";
        }
    }

    private string Did()
    {
        var what = Removed is [{ } only]
            ? $"'{Path.GetFileName(only.Path)}' ({FreeSpace.Format(only.Bytes)})"
            : $"{Removed.Count} items ({FreeSpace.Format(BytesRemoved)})";

        return Mode == ExploreRemovalMode.RecycleBin
            ? $"Moved {what} to the Recycle Bin. The drive gets the space once the bin is emptied."
            : $"Deleted {what}.";
    }
}

/// <summary>
/// Carries out an Explore removal (§7.1): refuse what the policy refuses, remove the rest, then
/// assert what should have survived did.
///
/// <para>The policy is consulted <em>here</em>, immediately before each deletion, rather than
/// trusted from the caller. A context menu that asked about the highlighted row and then deleted
/// the one under the pointer would otherwise be the only thing between a size picture and
/// <c>C:\Windows</c>, and a WinUI shell is exactly where that class of mistake cannot be
/// tested.</para>
///
/// <para>It removes one item at a time and never more than it was handed. §7.1 has no bulk action
/// and no pre-selection: what arrives here is what the user picked out by hand.</para>
/// </summary>
public static class ExploreRemover
{
    public static async Task<ExploreRemovalReport> RemoveAsync(
        IReadOnlyList<ExploreItem> items,
        ExploreRemovalMode mode,
        ExploreActionPolicy policy,
        IRecycleBin? recycleBin = null,
        IFileSystem? fileSystem = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(policy);

        var bin = recycleBin ?? ShellRecycleBin.Default;
        var fs = fileSystem ?? WindowsFileSystem.Default;

        var (allowed, refused) = Partition(items, policy);

        // The containers of the allowed items only. A refused item's container may be a directory
        // §5.2 says must never be listed at all — C:\Windows is the case that matters — and reading
        // it to build evidence about a deletion that is not going to happen would break the rule
        // this whole method exists to uphold.
        //
        // Listed before anything is deleted, because a survivor set gathered afterwards can only
        // describe what is left, which would agree with any removal however over-broad.
        //
        // Off the calling thread, and that is the purpose of the Task.Run rather than a detail of
        // it. The caller is a shell resuming on the UI thread after its own dialog, and this listing
        // is of a folder that may hold two hundred thousand entries. Everything after this stays on
        // the pool, because every await below is ConfigureAwait(false).
        var before = await Task.Run(() => Containers(allowed, fs), ct).ConfigureAwait(false);

        var outcomes = new List<ExploreItemOutcome>(items.Count);
        var cancelled = false;

        try
        {
            foreach (var item in allowed)
            {
                ct.ThrowIfCancellationRequested();

                outcomes.Add(await RemoveOneAsync(item, mode, bin, fs, ct).ConfigureAwait(false));
            }
        }
        catch (OperationCanceledException)
        {
            // Reported rather than thrown. §5.6 applies to a run that stopped part-way as much as to
            // one that finished, and more so: items are already gone, and letting the cancellation
            // unwind past the verification is the one moment the user would have no evidence at all
            // about what went with them.
            cancelled = true;
        }

        outcomes.AddRange(refused);

        return new ExploreRemovalReport(mode, outcomes, Verify(before, outcomes, fs), cancelled);
    }

    /// <summary>
    /// Which of <paramref name="items"/> the policy will take, and the refusals for the rest.
    ///
    /// <para>Public because a shell has to know before it asks. A confirmation dialog covering an
    /// item that is then refused teaches the user that saying yes is how you find out what
    /// happens, and §7.1 wants the reason stated instead. It changes nothing about who decides:
    /// <see cref="RemoveAsync"/> partitions again through this same method, so a caller that
    /// skipped it removes exactly as much.</para>
    /// </summary>
    /// <para>The allowed items come back with their paths <em>normalised</em>, which is not a
    /// tidy-up. Everything §5.6 asserts is derived from the path by splitting it: the container from
    /// <see cref="Path.GetDirectoryName(string)"/> and the leaf from
    /// <see cref="Path.GetFileName(string)"/>. A trailing separator makes the first return the item
    /// itself and the second return nothing, so the evidence would describe the directory being
    /// removed rather than the one it sits in, and a wholly correct removal would report as a
    /// failure. <see cref="LongPath.Configured"/> documents that trap where a provider's configured
    /// root meets it; a path arriving from a caller meets it here.</para>
    public static (IReadOnlyList<ExploreItem> Allowed, IReadOnlyList<ExploreItemOutcome> Refused) Partition(
        IReadOnlyList<ExploreItem> items,
        ExploreActionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(policy);

        var allowed = new List<ExploreItem>(items.Count);
        var refused = new List<ExploreItemOutcome>();

        foreach (var item in items)
        {
            if (policy.MayRemove(item.Path) is { IsAllowed: false } verdict)
            {
                refused.Add(new ExploreItemOutcome(item.Path, Removed: false, Bytes: 0, verdict.Reason));
                continue;
            }

            // Non-null by construction: the policy refuses outright anything Configured cannot
            // resolve, so a path that reaches this line has already been through it once.
            allowed.Add(item with { Path = LongPath.Configured(item.Path)! });
        }

        return (allowed, refused);
    }

    private static async Task<ExploreItemOutcome> RemoveOneAsync(
        ExploreItem item,
        ExploreRemovalMode mode,
        IRecycleBin bin,
        IFileSystem fs,
        CancellationToken ct)
    {
        if (mode == ExploreRemovalMode.RecycleBin)
        {
            // The display form, normalised: the shell namespace refuses the extended-length prefix
            // §6.3 requires everywhere else. IRecycleBin says why that is a second seam rather than
            // an argument to the first.
            var shellPath = LongPath.Display(LongPath.Extended(item.Path));
            var recycled = bin.Recycle(shellPath);

            return new ExploreItemOutcome(
                item.Path,
                recycled.Removed,
                recycled.Removed ? item.Bytes : 0,
                recycled.Message ?? "Moved to the Recycle Bin.");
        }

        if (!item.IsDirectory)
        {
            var file = await FileRemover.RemoveAsync(item.Path, ct, fs).ConfigureAwait(false);

            return new ExploreItemOutcome(
                item.Path,
                file.Removed,
                file.BytesReclaimed,
                file.Removed
                    ? "Deleted."
                    : "Left in place: Deguffer was refused. A file something else holds open and one "
                      + "this account may not touch are the same answer from here.");
        }

        var tree = await DirectoryRemover.RemoveAsync(item.Path, progress: null, ct, fs).ConfigureAwait(false);

        return new ExploreItemOutcome(
            item.Path,
            tree.RootRemoved,
            tree.BytesReclaimed,
            tree.RootRemoved
                ? Deleted(tree.Skipped)
                : $"Partly deleted — {tree.Skipped} item(s) are in use, so the folder is still there.");
    }

    private static string Deleted(int skipped) =>
        skipped == 0 ? "Deleted." : $"Deleted, apart from {skipped} item(s) something else is using.";

    /// <summary>
    /// The immediate contents of every directory a removal will take something out of, by name.
    ///
    /// <para>Names rather than paths, and one listing per container rather than one probe per
    /// sibling: a folder in a size picture can hold two hundred thousand entries, and §5.6's
    /// question — did anything <em>else</em> go? — is answered by comparing two listings, not by
    /// asking the disk about each of them twice.</para>
    /// </summary>
    private static Dictionary<string, IReadOnlyList<string>?> Containers(
        IReadOnlyList<ExploreItem> items, IFileSystem fs)
    {
        var containers = new Dictionary<string, IReadOnlyList<string>?>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            if (Path.GetDirectoryName(item.Path) is not { } parent || containers.ContainsKey(parent))
            {
                continue;
            }

            containers[parent] = Names(parent, fs);
        }

        return containers;
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

    /// <summary>
    /// §5.6. Two assertions per containing directory: that it is still there, and that everything
    /// beside the removed items is still in it.
    ///
    /// <para>The second is the one that bites. Asserting the target went away is half a test, and
    /// it is the half an over-broad removal passes.</para>
    /// </summary>
    private static VerificationResult Verify(
        Dictionary<string, IReadOnlyList<string>?> before,
        IReadOnlyList<ExploreItemOutcome> outcomes,
        IFileSystem fs)
    {
        // Keyed by the whole path rather than by the leaf name. Pooling names across containers
        // excuses a same-named sibling somewhere else: with 'projA\bin' removed, a removal that also
        // took 'projB\bin' would pass, and that is exactly the over-broad case this check exists
        // for.
        var removed = outcomes
            .Where(o => o.Removed)
            .Select(o => o.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var checks = new List<VerificationCheck>(before.Count * 2);

        foreach (var (parent, names) in before)
        {
            var survives = LongPath.DirectoryExists(parent);

            checks.Add(new VerificationCheck(
                parent,
                "The folder the item was taken out of must survive.",
                survives,
                survives ? "Still present." : "MISSING — it was there before the removal."));

            // A listing that never happened is recorded as a failure rather than as a pass. §5.6
            // is what turns "I think it worked" into evidence, and filing a non-assertion as
            // evidence is the one thing that undoes it — the more so here, because a passing report
            // says nothing at all, so the sentence explaining that this folder was never read would
            // reach nobody.
            checks.Add(names is null
                ? new VerificationCheck(
                    parent,
                    "Everything beside the removed item must survive.",
                    Passed: false,
                    "NOT ESTABLISHED — this folder would not list its contents, so nothing beside "
                    + "the removed item could be checked.")
                : SiblingsSurvived(parent, names, removed, fs));
        }

        return new VerificationResult { Checks = checks };
    }

    private static VerificationCheck SiblingsSurvived(
        string parent,
        IReadOnlyList<string> before,
        HashSet<string> removed,
        IFileSystem fs)
    {
        const string Reason = "Everything beside the removed item must survive.";

        if (Names(parent, fs) is not { } after)
        {
            return new VerificationCheck(
                parent, Reason, Passed: false,
                "Could not be checked: this folder listed its contents before the removal and would "
                + "not afterwards.");
        }

        bool WasRemoved(string name) => removed.Contains(Path.Combine(parent, name));

        var standing = after.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = before.Where(n => !WasRemoved(n) && !standing.Contains(n)).ToList();
        var expected = before.Count(n => !WasRemoved(n));

        return missing.Count == 0
            ? new VerificationCheck(
                parent, Reason, Passed: true, $"All {expected} other item(s) are still there.")
            : new VerificationCheck(
                parent, Reason, Passed: false,
                $"MISSING — {missing.Count} of {expected} other item(s) went too, starting with "
                + $"'{string.Join("', '", missing.Take(3))}'.");
    }
}
