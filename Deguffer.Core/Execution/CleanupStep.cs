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

    /// <summary>
    /// Whether this step removes something an unfinished Windows update may still need, so it runs
    /// only if Windows is not in the middle of one when the run reaches it.
    ///
    /// <para>The plan asked when it was made, and a preview can sit on screen while an update starts
    /// or a restart becomes owed. The run asks again immediately before the step, because a remembered
    /// answer is wrong in the direction that deletes. See <see cref="UnfinishedUpdate"/>.</para>
    /// </summary>
    public bool HeldWhileUpdating { get; init; }

    /// <summary>
    /// What this item is apart from its path, where its provider can say. Null for an item whose only
    /// name is where it is, which is an item nobody can keep. See <see cref="ItemIdentity"/>.
    ///
    /// <para>On every step rather than on a deletion alone, because an item is not always removed by
    /// Deguffer: a tool's own command can be the route for one item, and §5.1 prefers it. Such a step
    /// names what it removes in <see cref="RunCommandStep.Removes"/>, so a kept one is protected as a
    /// deletion is.</para>
    /// </summary>
    public ItemIdentity? Identity { get; init; }

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

    /// <summary>
    /// The paths this step is about, which §5.6 asserts are still standing where the step does not
    /// run: because the user declined it, or because it is on the keep list. Empty for a step whose
    /// reach nothing here can state.
    /// </summary>
    public abstract IReadOnlyList<string> Subjects { get; }
}

/// <summary>
/// What a tool writes into an item it has been told to remove, where it removes the item later
/// rather than at once.
///
/// <para><b>LM Studio on Windows is the case.</b> Its <c>lms runtime remove</c> writes a
/// <c>MARKED_FOR_DELETION</c> file into the runtime's folder and returns, and LM Studio deletes the
/// folder the next time it starts, because a loaded runtime holds its libraries open. So the run
/// frees nothing it can measure, and the step's figure is reported as scheduled rather than
/// reclaimed. The marker is also the only evidence that the command did anything: the tool reports
/// success before its own removal has run.</para>
/// </summary>
/// <param name="Marker">The file the tool writes directly inside the item.</param>
/// <param name="Remover">What will carry the removal out, named for the user.</param>
public sealed record ScheduledRemoval(string Marker, string Remover);

/// <summary>
/// Invoke a tool's own eviction command (§5.1) — always preferred over deleting paths, because
/// the tool knows about locations we do not.
/// </summary>
public sealed record RunCommandStep(string FileName, string Arguments, string What) : CleanupStep
{
    /// <summary>
    /// The one item this command is sent to remove, where it names one, in display form. Null for a
    /// command that clears a whole cache, whose reach is the tool's to decide.
    ///
    /// <para>It makes the command a choice between items rather than a part of one location. A
    /// declined or kept item is then protected under §5.6 as a declined deletion is, which is sound
    /// here and nowhere else: the command that would have removed it never ran, and every other
    /// command in the plan names a different item.</para>
    /// </summary>
    public string? Removes { get; init; }

    /// <summary>
    /// How the tool marks <see cref="Removes"/> when it removes it later rather than at once. Null for
    /// a tool that removes its item before the command returns.
    /// </summary>
    public ScheduledRemoval? Scheduled { get; init; }

    /// <summary>
    /// Programs one of which must be running when the command runs. Empty for a command that needs
    /// none.
    ///
    /// <para><b>For a tool that starts its own application when it finds it closed.</b> <c>lms</c> is
    /// the case: it launches LM Studio as a service rather than fail. The plan was made while the
    /// program ran, and the user can close it while the preview is on screen, so the run asks again
    /// immediately before the command and does not run it if the program is gone.</para>
    /// </summary>
    public IReadOnlyList<string> RunsOnlyWhile { get; init; } = [];

    /// <summary>
    /// <see cref="Removes"/>, where the command names an item. <see cref="MeasuredPaths"/> never
    /// counts: it is a probe of where the tool was sent, and the tool decides what it takes there.
    /// </summary>
    public override IReadOnlyList<string> Subjects => Removes is { } item ? [item] : [];

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
/// Ask a sync app to release the local copies of files it keeps in step with the cloud, leaving every
/// one of them where it is (<c>docs/todo/unreached-locations.md</c> §10).
///
/// <para><b>Nothing is destroyed, so nothing here is a target.</b> Each file stays in its folder, lists,
/// and opens while the machine is online, so this step adds nothing to
/// <see cref="CleanupPlan.TargetedPaths"/>, and its §5.6 negative asserts the opposite of a
/// deletion's: that every file it named is still there and is still a cloud file.</para>
///
/// <para><b>What it frees is a request, never a result.</b> Windows' own documentation for
/// <c>CF_PIN_STATE_UNPINNED</c> says there is "no guarantee that the placeholders to be unpinned will be
/// fully dehydrated after the API call completes successfully", and on a scratch sync root unpinning
/// alone released nothing: releasing is the sync app's work. So <see cref="CleanupStep.Estimated"/> is what the files
/// held when they were chosen, and a run reports it as requested
/// (<see cref="StepOutcome.BytesRequested"/>), never as reclaimed.</para>
///
/// <para><b>The files are named, and the rules are asked again.</b> The run acts on these files and no
/// others, so the preview is what runs. Each is looked at again through the handle that unpins it,
/// because any of them may have gained an edit, a pin or a pinned folder in the meantime. See
/// <see cref="Cloud.ReleaseRules"/>.</para>
/// </summary>
/// <param name="SyncRoot">The folder the sync app owns, which is what identifies this step.</param>
/// <param name="SyncApp">The sync app that will do the releasing, named for the user.</param>
/// <param name="What">What these files are, written for the user.</param>
public sealed record ReleaseLocalCopiesStep(string SyncRoot, string SyncApp, string What) : CleanupStep
{
    /// <summary>The placeholders chosen when the plan was made, and what each held on this PC then.</summary>
    public IReadOnlyList<Cloud.ReleasableFile> Files { get; init; } = [];

    /// <summary>
    /// The folder, not the files: which files are eligible changes with every edit and every download,
    /// and a key that moved with them would discard the user's choice about the account each time.
    /// </summary>
    public override string SelectionKey => SyncRoot;

    public override string Description => $"{What} — {LongPath.Display(SyncRoot)}";

    /// <summary>Nothing: the step destroys nothing, so a declined one leaves nothing that could have gone.</summary>
    public override IReadOnlyList<string> Subjects => [];
}

/// <summary>
/// A step that destroys one path outright.
///
/// The base exists so that "everything this plan would remove" is one question with one answer:
/// <see cref="CleanupPlan.TargetedPaths"/> and <see cref="CleanupPlan.NarrowedTo"/> both select on
/// this type, so a new kind of deletion joins the §5.2 assertions and the §5.6 negative by
/// construction rather than by somebody remembering to update two <c>OfType</c> clauses.
///
/// It is deliberately narrower than "a new kind of step". <see cref="ReleaseLocalCopiesStep"/> frees
/// space while leaving every file present and readable, so it is a sibling of this and of
/// <see cref="RunCommandStep"/> under <see cref="CleanupStep"/> — and it must *not* appear in
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
    /// Every path carrying this step out destroys: <see cref="Path"/>, and for a step Windows carries
    /// out, whatever else Windows is registered to clear with it.
    ///
    /// <para><b>It is what §5.6 reads, so it may never be narrower than the removal.</b>
    /// <see cref="CleanupPlan.TargetedPaths"/>, the protection a declined step leaves behind, and the
    /// search for an Outlook data file before the step runs all ask this rather than
    /// <see cref="Path"/>. Windows' own <em>Windows ESD installation files</em> cleanup is the case:
    /// one row, and three directories go.</para>
    /// </summary>
    public virtual IReadOnlyList<string> Destroys => [Path];

    /// <summary>What the removal destroys, which is what a declined or kept one leaves standing.</summary>
    public override IReadOnlyList<string> Subjects => Destroys;

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
    /// The question this step was offered on the answer to, where a program starting could change
    /// that answer before the clean. The run asks it again immediately before the step. Null for a
    /// step whose offer rests on nothing a program can change. See <see cref="IUseCheck"/>.
    /// </summary>
    public IUseCheck? UseCheck { get; init; }
}

/// <summary>
/// Delete one explicitly recognised directory. Never a tool root — see <see cref="DisposableChildSet"/>.
/// </summary>
public sealed record DeleteDirectoryStep(string Path, string What) : DeleteStep(Path, What)
{
    /// <summary>
    /// Whether this directory goes as a whole or not at all, because its parts only mean something
    /// together.
    ///
    /// <para><b>A Recycle Bin removed file by file is the case.</b> Each deleted item is two entries —
    /// its content and the record Windows keeps beside it to restore it — so a removal that stepped
    /// over an Outlook mail store inside a deleted folder would keep the store and take its record,
    /// leaving it on the disk with nothing able to put it back (§9). A step like that is withheld
    /// while it holds a store, and looked at again on the disk immediately before it runs, as
    /// <see cref="EmptyRecycleBinStep"/> is.</para>
    ///
    /// <para>False by default, which is every other removal: a cache or a build directory is a
    /// collection of independent files, and stepping over a store in one leaves the rest meaningful.</para>
    /// </summary>
    public bool IsIndivisible { get; init; }

    /// <summary>
    /// Whether this directory is removed only when every part of it can be: nothing inside is newer
    /// than the plan's guard, and every folder inside can be listed. Otherwise nothing is removed.
    ///
    /// <para>Stronger than <see cref="IsIndivisible"/>, which is about Outlook data files alone. The
    /// ordinary removal keeps what the guard protects and what Windows refuses and takes the rest,
    /// which is right for a cache and wrong for a folder such as <c>$WinREAgent</c>, where a rollback
    /// manifest means nothing without the image it restores. Looked at on the disk immediately before
    /// the removal, through <see cref="WholeTreeLook"/>.</para>
    /// </summary>
    public bool IsAllOrNothing { get; init; }

    /// <summary>
    /// Directories holding the index of what <see cref="DeleteStep.Path"/> holds, which go with it: each
    /// is removed first, and <see cref="DeleteStep.Path"/> is touched only once every one of them is
    /// gone. Empty for every removal whose content nothing else points into.
    ///
    /// <para><b>Zig's global cache is the case.</b> A manifest in its index names an output by where it
    /// is, and trusts that it is still there: an output removed from under a manifest that stayed is a
    /// failed build, not a slower one, and it stays failed until somebody clears the cache by hand. An
    /// index removed on its own is only a cache miss. So the order is the safety, and it is one step
    /// because a part the user could leave out, or a folder Explore could take alone, would undo it.</para>
    ///
    /// <para><b>It is all or nothing by construction</b>, as <see cref="IsAllOrNothing"/> describes,
    /// whether or not that is set: a manifest the guard on recently changed files kept would name an
    /// output nobody can promise is still there. So a plan whose guard would hold anything back
    /// withdraws the step, and the run looks at the disk again before it starts.</para>
    /// </summary>
    public IReadOnlyList<string> IndexedBy { get; init; } = [];

    /// <summary>The index first, because that is the order the run removes them in.</summary>
    public override IReadOnlyList<string> Destroys => [.. IndexedBy, Path];

    /// <summary>
    /// Whether the run looks at the whole of <see cref="Destroys"/> on the disk first, and removes nothing
    /// if anything it finds would leave a part standing: a recent file, a folder that would not be
    /// listed, or an Outlook data file. So a plan must not offer such a step while it holds a store.
    /// </summary>
    public bool GoesWholeOrNotAtAll => IsAllOrNothing || IndexedBy.Count > 0;

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

    /// <summary>
    /// Entries directly inside <see cref="DeleteStep.Path"/> that another row offers under the name of
    /// the tool that wrote them, so this step neither takes nor counts them. See
    /// <see cref="Providers.ITemporaryFolderTenant"/>.
    /// </summary>
    public IReadOnlyList<string> OwnedElsewhere { get; init; } = [];

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
/// Hand one Disk Cleanup handler Windows registers the volume it clears, rather than deleting what it
/// clears (§5.1).
///
/// <para><b>Why the handler and never the path.</b> <c>Windows.old</c> is the case. Windows' own
/// <em>Previous Installations</em> registration asks its handler to remove the uninstall record as
/// well (<c>RemoveUninstall</c>), which is what Settings offers to go back from, and a tree removed by
/// path would leave that record naming a folder that is no longer there. The handler is Windows'
/// statement of what goes with the folder, and Deguffer does not know it.</para>
///
/// <para><b>Why it is a <see cref="DeleteStep"/>, for the reason <see cref="EmptyRecycleBinStep"/>
/// is one.</b> It destroys paths the plan named, sized and dated, so it belongs where §5.6's negative
/// and <see cref="CleanupPlan.TargetedPaths"/> look. What it shares with a <see cref="RunCommandStep"/>
/// is that Windows, not Deguffer, decides what goes. So the run's reach is unbounded while one is in
/// it (see <see cref="RunReach.Unbounded"/>), and it goes whole or not at all, which is why an Outlook
/// data file inside it withholds it and why a guard on recently changed files that would hold anything
/// back withdraws it.</para>
/// </summary>
/// <param name="Path">The directory the row is about, in display form.</param>
/// <param name="What">Why it is disposable, written for the user.</param>
public sealed record DiskCleanupStep(string Path, string What) : DeleteStep(Path, What)
{
    /// <summary>
    /// The handler's name, as Windows registers it under
    /// <c>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches</c>.
    /// </summary>
    public required string Handler { get; init; }

    /// <summary>
    /// The top of the volume the handler is asked to clear, in display form: the handler takes
    /// <c>C:\</c>, never <c>\\?\C:\</c>.
    /// </summary>
    public required string Volume { get; init; }

    /// <summary>
    /// The directories besides <see cref="DeleteStep.Path"/> that Windows registers the handler to
    /// clear, and that were there when the plan was made. Empty for a handler with one directory.
    /// </summary>
    public IReadOnlyList<string> AlsoClears { get; init; } = [];

    public override IReadOnlyList<string> Destroys => [Path, .. AlsoClears];

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
