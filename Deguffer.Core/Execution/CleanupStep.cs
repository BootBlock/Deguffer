using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>One action in a plan. Nothing here has been executed.</summary>
public abstract record CleanupStep
{
    /// <summary>What the user is told this step will do.</summary>
    public abstract string Description { get; }

    /// <summary>
    /// What identifies this step from one scan to the next, so a choice the user made about it can
    /// be matched to the same step later. Unique within one provider's plan; nothing compares keys
    /// across providers.
    ///
    /// Deliberately not <see cref="Description"/>, which is prose: rewording a sentence would
    /// silently discard every choice the user had made about that step, and the direction it
    /// discards them in is back towards the pre-selected default.
    /// </summary>
    public abstract string SelectionKey { get; }

    /// <summary>
    /// What this step is expected to reclaim, measured at plan time.
    ///
    /// A <see cref="ScanSize"/> rather than a bare count because allocated and logical bytes are
    /// legitimately different numbers on compressed and sparse trees, and because this is where
    /// §5.4's second pair — reclaimed inside a virtual disk versus on the host — will belong when a
    /// container provider arrives.
    /// </summary>
    public ScanSize Estimated { get; init; }

    /// <summary>
    /// The single number to show and to subtract. It is the logical figure, which is not the same
    /// as what the volume gives back — see <see cref="ScanSize.Reclaimable"/> for the measurements
    /// that chose it over the allocated one, and for where the honest free-space answer comes from.
    /// </summary>
    public long EstimatedBytes => Estimated.Reclaimable;

    /// <summary>
    /// What choosing this step reclaims, as the shell states it and adds it up: its bytes, and — for a
    /// leftover — its entries.
    ///
    /// <para>Separate from <see cref="Estimated"/>, which is what was measured. Every deletion is
    /// measured in entries, and for most of them the count is not what they are offered for: an empty
    /// cache folder is an entry its tool puts straight back. See <see cref="DeleteStep.IsLeftover"/>
    /// for why only a leftover's entries are part of what choosing it reclaims.</para>
    /// </summary>
    public virtual ScanSize Reclaim => Estimated with { Entries = 0 };

    /// <summary>
    /// Whether carrying this step out removes anything worth choosing it for. Asked of
    /// <see cref="Reclaim"/> and nothing else, so the checkbox, the row's status and the totals beside
    /// it cannot come to disagree about what "nothing to remove" means.
    /// </summary>
    public bool RemovesSomething => Reclaim.Reclaimable > 0 || Reclaim.Entries > 0;

    /// <summary>
    /// When this step's subject was last written, or null where the provider cannot tell.
    ///
    /// §7 makes age a first-class column for per-workspace and per-project data, on the grounds
    /// that "last touched 5 months ago" drives the decision more than size does. Null is a real
    /// answer and must stay distinguishable from an old one — <see cref="RelativeAge"/> renders it
    /// as unknown, never as an age, because an age is what invites the user to delete something.
    ///
    /// Whole-cache steps leave this null: a single timestamp across a tool's entire cache would be
    /// a number with no meaning attached to it.
    /// </summary>
    public DateTime? LastWritten { get; init; }

    /// <summary>
    /// Whether carrying this step out needs administrator rights.
    ///
    /// A different claim from <see cref="FallbackReason.NotElevated"/>, which says a *measurement*
    /// took the slow route. This one says the step can be seen and cannot be performed, and the two
    /// are independent: a location under <c>C:\Windows</c> needs elevation to remove however its
    /// size was arrived at. Both are answered by the same offer, and
    /// <see cref="ElevationOffer"/> reads both.
    ///
    /// A declaration by the provider rather than something derived from the path. Deriving it would
    /// mean a rule about which directories Windows protects, which is exactly the kind of guess §5.2
    /// refuses; declared, it is checkable by reading the provider's own table.
    ///
    /// It is a fact about the target, not about the run, so it stays true on an elevated process.
    /// Who may act on it is the shell's question, and it asks it by pairing this with the token it
    /// is actually running under.
    /// </summary>
    public bool RequiresElevation { get; init; }

    /// <summary>
    /// Whether the user's guard on recently changed files left something real out of
    /// <see cref="Estimated"/>.
    ///
    /// <para>It sits here rather than on the plan because only the measurement knows it, and
    /// measuring happens per path. <see cref="CleanupPlan.HasRecentContentHeldBack"/> is the
    /// question the shell actually asks, and it is that question asked over these.</para>
    ///
    /// <para>False on nearly every <see cref="RunCommandStep"/>: §5.1 leaves the tool's own eviction
    /// command deciding what it removes, so nothing is held back from it and its figure is the whole
    /// cache. <see cref="Providers.FileHistoryProvider"/> is the exception, and it is one because
    /// the <em>command</em> takes an age — <c>FhManagew.exe -cleanup &lt;days&gt;</c> considers only
    /// versions past a cut-off, so the estimate is the aged part of the folder by construction. A
    /// target full of recent versions measures zero and is not clear, which is exactly the false
    /// "Already clear" this flag exists to prevent.</para>
    /// </summary>
    public bool WithheldRecent { get; init; }

    /// <summary>
    /// What Windows still would not release here, of the places the last clean of this step was
    /// refused — already taken out of <see cref="Estimated"/>.
    ///
    /// <para>Carried as well as subtracted, for the reason <see cref="WithheldRecent"/> is: once
    /// something is left out, a zero is ambiguous, and the shell makes a claim out of a zero. "Already
    /// clear" beside a folder holding gigabytes Windows will not let go is the same false sentence in
    /// a different place. See <see cref="RefusalRecord"/> for why only those places are asked.</para>
    /// </summary>
    public Refusals Refused { get; init; }

    /// <summary>
    /// The Outlook mail stores this step's measurement found inside what it would remove, already
    /// left out of <see cref="Estimated"/>. See <see cref="MailStore"/>.
    ///
    /// <para>Carried for the plan to act on rather than for the removal, which leaves a store without
    /// being told to. A removal Deguffer performs itself steps over each one, so its step stays and
    /// does less; a step that cannot — a tool's own command, or Windows emptying a Recycle Bin whole —
    /// has to be withheld, and only this list says which steps those are before anything runs.</para>
    /// </summary>
    public IReadOnlyList<string> MailStores { get; init; } = [];
}

/// <summary>
/// Invoke a tool's own eviction command (§5.1) — always preferred over deleting paths, because
/// the tool knows about locations we do not.
/// </summary>
public sealed record RunCommandStep(string FileName, string Arguments, string What) : CleanupStep
{
    /// <summary>
    /// The locations we expect the command to clear. The command remains the authority on *what*
    /// gets removed, which is the whole point of §5.1. NuGet's own clear reached two locations that
    /// were not under <c>.nuget</c> at all, so this list is a probe, never a target.
    ///
    /// <para>It has two readers, and a path added for one reaches the other. The executor measures
    /// these locations again to report what the command reclaimed. And §5.6 reads them as where the
    /// tool was sent: a protected folder holding one of them may end the run empty without an alarm
    /// (see <see cref="RunReach.ProbedPaths"/>). So a location listed only to improve the figure
    /// also excuses every protected folder above it.</para>
    /// </summary>
    public IReadOnlyList<string> MeasuredPaths { get; init; } = [];

    /// <summary>
    /// What <see cref="MeasuredPaths"/> held at plan time, where that is a different number from
    /// <see cref="CleanupStep.Estimated"/>. Null means they are the same figure, which is every
    /// provider whose estimate *is* its measurement of those paths.
    ///
    /// <para>Exists for the provider whose estimate is the tool's own accounting rather than
    /// Deguffer's: conda's dry run reports what its clean will free, while its package caches
    /// measure far larger, because everything an environment hard-links stays. Reporting the
    /// reclaim as "estimate minus what remains" would then compare two different kinds of number
    /// and call the result negative. The delta must subtract like from like, so the step carries
    /// Deguffer's own plan-time probe of the same paths the executor re-measures.</para>
    ///
    /// <para><b>It fixes the pairing, and the re-measurement is fixed elsewhere.</b> The "after"
    /// figure comes from the provider's own scanner, and <see cref="Scanning.DirectoryScanner"/>
    /// holds its volume index until something invalidates it — which happens once, at the start of a
    /// planning pass. Every command step therefore used to subtract two readings of one pre-command
    /// snapshot and report nothing reclaimed. That belonged to the executor rather than to any
    /// provider, and it is closed by
    /// <see cref="Scanning.IDirectoryScanner.MeasureFromDiskAsync"/>, which the executor's
    /// after-measure takes.</para>
    /// </summary>
    public ScanSize? MeasuredBefore { get; init; }

    public override string Description => $"{What} ({Path.GetFileName(FileName)} {Arguments})";

    /// <summary>
    /// The command itself, without where the tool happens to be installed. A tool that moves — an
    /// upgrade that lands under a new version directory, a PATH entry that resolves elsewhere — is
    /// running the same command on the same cache, and the user's choice about it still applies.
    /// </summary>
    public override string SelectionKey => $"{Path.GetFileName(FileName)} {Arguments}";
}

/// <summary>
/// A step that destroys one path outright.
///
/// The base exists so that "everything this plan would remove" is one question with one answer:
/// <see cref="CleanupPlan.TargetedPaths"/> and <see cref="CleanupPlan.NarrowedTo"/> both select on
/// this type, so a new kind of deletion joins the §5.2 assertions and the §5.6 negative by
/// construction rather than by somebody remembering to update two <c>OfType</c> clauses.
///
/// It is deliberately narrower than "a new kind of step". The cloud-sync dehydration in
/// <c>docs/todo/unreached-locations.md</c> §10 frees space while leaving the file present and
/// readable, so it will be a sibling of this and of <see cref="RunCommandStep"/> under
/// <see cref="CleanupStep"/> — and it must *not* appear in
/// <see cref="CleanupPlan.TargetedPaths"/>, because it destroys nothing.
/// </summary>
/// <param name="Path">The path that will be removed, in display form.</param>
/// <param name="What">Why it is disposable, written for the user.</param>
public abstract record DeleteStep(string Path, string What) : CleanupStep
{
    /// <summary>
    /// The path, which is the whole of what a deletion is about. Answered here rather than on each
    /// concrete step so a new kind of deletion cannot arrive keyed on something else.
    /// </summary>
    public override string SelectionKey => Path;

    /// <summary>
    /// What this item is apart from its path, where its provider can say. Null for an item whose only
    /// name is where it is, which is an item nobody can keep. See <see cref="ItemIdentity"/>.
    /// </summary>
    public ItemIdentity? Identity { get; init; }

    /// <summary>
    /// Whether the path itself is what is being reclaimed — something nothing will create again —
    /// rather than a folder its owner re-creates the next time it runs.
    ///
    /// <para><b>It decides whether a step that frees no bytes may be chosen.</b> An empty cache folder
    /// is an entry the removal takes, and its tool puts it back at its next launch: offering it would
    /// keep a row at "Ready to clean" permanently while freeing nothing that lasts. An empty folder a
    /// session left behind after it ended comes back never, and removing it is the whole of what its
    /// provider is for. Nothing about the two folders tells them apart, so the provider that knows
    /// what wrote the path says which it is.</para>
    ///
    /// <para>False by default, which is every cache: a step then needs bytes to be chosen, exactly as
    /// before entries were counted.</para>
    /// </summary>
    public bool IsLeftover { get; init; }

    public override ScanSize Reclaim => IsLeftover ? Estimated : base.Reclaim;

    /// <summary>
    /// What a reader choosing between items is told about this one beyond its path, in the order its
    /// provider wants the columns. Empty for most steps. See <see cref="ItemFacet"/>.
    /// </summary>
    public IReadOnlyList<ItemFacet> Facets { get; init; } = [];

    /// <summary>
    /// The heading this item is listed under, such as the approved folder a project was found in or the
    /// browser profile a cache belongs to. Null where its provider's items fall under no heading.
    ///
    /// <para>Presentation only, like <see cref="Facets"/>. A heading lets hundreds of items be read and
    /// chosen a group at a time. Nothing about a run depends on which group a step is in.</para>
    /// </summary>
    public string? Group { get; init; }
}

/// <summary>
/// Delete one explicitly recognised directory. Never a tool root — see <see cref="DisposableChildSet"/>.
/// </summary>
public sealed record DeleteDirectoryStep(string Path, string What) : DeleteStep(Path, What)
{
    public override string Description => $"{What} — {LongPath.Display(Path)}";
}

/// <summary>
/// Empty one directory in place: everything inside it goes, and the directory itself stays.
///
/// <para><b>It exists because <c>%TEMP%</c> is not Deguffer's to take away.</b> Every program on the
/// machine expects the folder to be there, Windows does not put it back once it is gone, and a
/// profile whose scratch folder has been deleted is one where the next installer fails for a reason
/// nobody will connect to a disk cleaner. §5.2 says the same thing in its own words: never delete a
/// tool's root directory. What is disposable here is the contents, and the contents are what this
/// step names.</para>
///
/// <para><b><see cref="Spared"/> is the other half of §5.3, and it cannot be an age.</b> An entry a
/// running program is working in is off limits however old its files are — the process holding it
/// may have opened nothing this minute, so nothing is locked and the timestamps prove nothing. The
/// plan therefore names those entries, and it names them for the user as well, because §5.6 asserts
/// every one of them is still standing afterwards.</para>
///
/// <para><see cref="CleanupStep.SelectionKey"/> is inherited, and is the folder rather than the set:
/// which entries are live changes between one preview and the next, and a key that moved with them
/// would discard the user's choice about the folder every time a program started or stopped.</para>
/// </summary>
public sealed record ClearDirectoryStep(string Path, string What) : DeleteStep(Path, What)
{
    /// <summary>
    /// Entries directly inside <see cref="DeleteStep.Path"/> that this step must leave alone,
    /// because something is using them. Empty where nothing was found to be.
    /// </summary>
    public IReadOnlyList<string> Spared { get; init; } = [];

    public override string Description => $"{What} — {LongPath.Display(Path)}";
}

/// <summary>
/// Empty one volume's Recycle Bin through Windows rather than by deleting its files.
///
/// <para><b>Why it is a <see cref="DeleteStep"/> and not a <see cref="RunCommandStep"/>.</b> §5.1's
/// preferred route is a tool's own eviction command, and <c>SHEmptyRecycleBin</c> is one — but a
/// command step reports against probe paths and is never guarded, and neither is true here. This
/// destroys one path whose contents the plan named, sized and dated, so it belongs where §5.6's
/// negative and <see cref="CleanupPlan.TargetedPaths"/> already look.</para>
///
/// <para><b><see cref="CleanupStep.SelectionKey"/> is inherited, which is the point.</b> The key is
/// the path for every deletion, so a bin the user ticked keeps its tick when the route changes
/// underneath it — the setting that chooses between this step and
/// <see cref="DeleteDirectoryStep"/> must not silently discard a selection, and the direction it
/// would discard it in is back towards not being selected.</para>
///
/// <para>The shell is given the volume, and the plan names the account's directory inside it. Those
/// are different paths on purpose: <see cref="VolumeRoot"/> is what the call accepts, and
/// <see cref="DeleteStep.Path"/> is what actually loses its contents and what §5.6 measures around.
/// See <see cref="ShellRecycleBinEmptier"/> for what was observed about the gap between them.</para>
/// </summary>
public sealed record EmptyRecycleBinStep(string Path, string What) : DeleteStep(Path, What)
{
    /// <summary>
    /// The volume whose bin this is, which is what <c>SHEmptyRecycleBin</c> accepts, or an empty
    /// string where <see cref="DeleteStep.Path"/> is not shaped like a bin at all.
    ///
    /// <para>Derived rather than stored, because the shape is guaranteed by the only thing that
    /// builds one: a bin is always <c>&lt;root&gt;\$Recycle.Bin\&lt;account&gt;</c>, so the root is
    /// two levels up. Deriving it this way rather than with <c>GetPathRoot</c> is deliberate — the
    /// tests stand synthetic volumes inside a scratch directory, where the drive's own root is not
    /// the volume the step means, and a derivation that could not be exercised there would be one
    /// nothing checks.</para>
    ///
    /// <para><b>Nothing is the answer for a path that is not two levels deep, and the alternative
    /// was dangerous.</b> Falling back to the path itself reads as harmless, and is exactly wrong
    /// for the one input that reaches the fallback while still being accepted downstream: a
    /// <see cref="DeleteStep.Path"/> that is already a drive root. <c>GetDirectoryName</c> answers
    /// null for it, the fallback would hand that root straight to the shell, and
    /// <see cref="ShellRecycleBinEmptier"/>'s guard admits a root. A malformed target would have
    /// become a whole-volume call. An empty string is refused by everything downstream, so the
    /// degenerate case fails closed.</para>
    ///
    /// <para><see cref="LongPath.Display"/> first, because the shell namespace refuses the
    /// extended-length prefix and because trimming it after splitting would leave <c>\\?\D:</c>
    /// as the root. See <see cref="IRecycleBinEmptier"/>.</para>
    /// </summary>
    public string VolumeRoot =>
        System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(LongPath.Display(Path)))
        ?? string.Empty;

    public override string Description => $"{What} — {LongPath.Display(Path)}";
}

/// <summary>
/// Delete one explicitly named file.
///
/// Exists for <c>C:\Windows\MEMORY.DMP</c>, which is a single file and the largest single reclaim
/// Deguffer knows about — the size of installed memory after one stop error on a machine configured
/// to write a complete dump. There is no directory to target: the file's parent is the Windows
/// directory itself, which §5.2 forbids touching and this provider never enumerates.
///
/// A file is not a small tree, and the difference is a safety one rather than a convenience. A
/// directory removal walks and can partially succeed; this either removes the one path named or
/// leaves it, so <see cref="FileRemover"/> is a few lines rather than a reuse of
/// <see cref="DirectoryRemover"/> with the walking suppressed.
/// </summary>
public sealed record DeleteFileStep(string Path, string What) : DeleteStep(Path, What)
{
    public override string Description => $"{What} — {LongPath.Display(Path)}";
}
