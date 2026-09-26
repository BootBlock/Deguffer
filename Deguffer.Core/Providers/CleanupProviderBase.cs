using System.Collections.Concurrent;
using Deguffer.Core.Cloud;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The shared shape of a provider: it supplies rules, and delegates carrying them out.
///
/// Execution lives in <see cref="PlanExecutor"/> and survival policy in <see cref="PlanVerifier"/>,
/// so a subclass contains nothing but knowledge about one cache.
/// </summary>
public abstract class CleanupProviderBase : ICleanupProvider
{
    /// <summary>
    /// The Outlook mail stores the measurements behind the plan being built have met, for
    /// <see cref="PlanAsync"/> to give to the commands whose reach holds them (§9).
    ///
    /// <para><b>Collected here because every measurement a provider makes goes through this class</b>,
    /// and a command step is built by hand in each provider that has one. Asking each of them to copy
    /// what its measurement found onto its step is the parallel edit one of them forgets, and the one
    /// that forgets runs a tool over a store. This way no provider can.</para>
    ///
    /// <para>Scoped to one call of <see cref="PlanAsync"/> by the asynchronous flow it runs in, so plans
    /// built at the same time — the planner builds them in parallel — never see each other's stores.
    /// Static because the flow, not the instance, is what it is keyed by (G5).</para>
    /// </summary>
    private static readonly AsyncLocal<ConcurrentBag<string>?> MeasuredMailStores = new();

    private readonly PlanExecutor _executor;
    private readonly RefusalRecord _refusals;

    /// <param name="emptier">
    /// How a <see cref="EmptyRecycleBinStep"/> is carried out, for the one provider that plans one.
    /// Defaulted rather than required because every other provider has no use for it, and injected
    /// rather than reached for because the real one empties the Recycle Bin of whoever runs the
    /// suite.
    /// </param>
    /// <param name="cloud">
    /// How a <see cref="ReleaseLocalCopiesStep"/> is planned, carried out and proved, for the one provider
    /// that plans one. Defaulted and injected for the reason <paramref name="emptier"/> is: the real one
    /// acts on the cloud accounts of whoever runs the suite.
    /// </param>
    /// <param name="handlers">
    /// How a <see cref="DiskCleanupStep"/> is carried out, for the providers that plan one. Defaulted
    /// and injected for the reason <paramref name="emptier"/> is: the real one deletes the previous
    /// Windows installation of whoever runs the suite.
    /// </param>
    /// <param name="servicing">
    /// Where Windows is in servicing itself, asked when a plan is made and again before a step that is
    /// <see cref="CleanupStep.HeldWhileUpdating"/> runs, by the same instance for the reason
    /// <see cref="Emptier"/> is shared.
    /// </param>
    /// <param name="time">
    /// The clock a command step waits on while its tool marks what it will remove later. See
    /// <see cref="PlanExecutor"/>.
    /// </param>
    protected CleanupProviderBase(
        IUserEnvironment environment,
        IProcessRunner runner,
        IProcessInspector inspector,
        IDirectoryScanner scanner,
        IRecycleBinEmptier? emptier = null,
        ICloudFiles? cloud = null,
        IDiskCleanupHandlers? handlers = null,
        IWindowsServicing? servicing = null,
        TimeProvider? time = null)
    {
        Environment = environment;
        Inspector = inspector;
        Scanner = scanner;
        _refusals = RefusalRecord.For(environment);
        Emptier = emptier ?? ShellRecycleBinEmptier.Default;
        Cloud = cloud ?? CloudFiles.Default;
        Handlers = handlers ?? DiskCleanupHandlers.Default;
        Servicing = servicing ?? WindowsServicing.Current;
        _executor = new PlanExecutor(runner, scanner, _refusals, Emptier, Cloud, Handlers, Servicing, inspector, time);
        Runner = runner;
    }

    protected IUserEnvironment Environment { get; }

    /// <summary>
    /// The route a <see cref="EmptyRecycleBinStep"/> takes, resolved here rather than left to the
    /// executor so that a provider choosing between it and removing the files itself asks the same
    /// instance the step will be carried out by. Two defaults would let a plan choose a route on
    /// one object's answer and then be executed by another.
    /// </summary>
    protected IRecycleBinEmptier Emptier { get; }

    /// <summary>
    /// The Cloud Files API a <see cref="ReleaseLocalCopiesStep"/> is planned through, and the same one it
    /// is carried out and proved through, for the reason <see cref="Emptier"/> is shared.
    /// </summary>
    protected ICloudFiles Cloud { get; }

    /// <summary>
    /// The handlers a <see cref="DiskCleanupStep"/> is carried out by, asked whether each is there by
    /// the same instance that will run it, for the reason <see cref="Emptier"/> is shared.
    /// </summary>
    protected IDiskCleanupHandlers Handlers { get; }

    /// <summary>Where Windows is in servicing itself. See the constructor's parameter.</summary>
    protected IWindowsServicing Servicing { get; }

    protected IProcessRunner Runner { get; }

    protected IProcessInspector Inspector { get; }

    /// <summary>
    /// How this provider learns sizes. §5.5's choice between reading the MFT and walking the tree
    /// lives entirely behind this interface: a provider states which paths it cares about and is
    /// told how big they are, and nothing here knows there are two strategies.
    /// </summary>
    protected IDirectoryScanner Scanner { get; }

    public abstract string Id { get; }

    public abstract string Name { get; }

    public abstract SafetyTier Tier { get; }

    public abstract StepGrain Grain { get; }

    public abstract string WhatHappensOnNextUse { get; }

    public abstract ProviderDescription Description { get; }

    /// <summary>
    /// Processes that, if running, mean this tool's state may be live (§5.3). Their presence is a
    /// warning on the plan, not a refusal.
    /// </summary>
    protected virtual IReadOnlyList<string> ConflictingProcessNames => [];

    public abstract Task<bool> IsPresentAsync(CancellationToken ct = default);

    /// <summary>
    /// False for a cache provider: it knows where its own cache lives and needs no permission to
    /// look there. The providers that search the user's source trees override it.
    /// </summary>
    public virtual bool IsAwaitingSourceFolders => false;

    public virtual void InvalidateCaches()
    {
        Environment.Invalidate();
        Inspector.Invalidate();
        Scanner.Invalidate();
    }

    /// <summary>
    /// Nothing, for a provider that owns no directory whose unrecognised children must be
    /// protected. Most do not: a provider whose target <em>is</em> its cache directory has no
    /// siblings to spare, and one that finds its roots rather than knowing them cannot answer here
    /// cheaply. The providers that do own such a root override this.
    /// </summary>
    public virtual IReadOnlyList<ToolRoot> ToolRoots => [];

    /// <summary>
    /// Nothing, for the majority whose <see cref="ToolRoots"/> already say everything they own. A
    /// provider whose location a subprocess reports, or one that holds a path back because
    /// something is using it, overrides this.
    /// </summary>
    public virtual Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ToolRoot>>([]);

    /// <summary>
    /// The plan this provider builds, with the user's guard on recently touched files stamped onto
    /// it and its consequences applied.
    ///
    /// <para>Sealed here rather than left to each provider, because both halves of the guard are
    /// things a provider must not be able to forget. A plan that did not carry
    /// <see cref="CleanupPlan.Keep"/> would be executed as though no guard existed, and a provider
    /// constructs one in sixty-nine places across this project — twenty-two <c>new CleanupPlan</c>
    /// and forty-seven <see cref="EmptyPlan"/>. So it is stamped once, on whatever comes back.</para>
    /// </summary>
    public async Task<CleanupPlan> PlanAsync(MinimumAge keep = default, CancellationToken ct = default)
    {
        var measured = new ConcurrentBag<string>();
        MeasuredMailStores.Value = measured;

        var built = await BuildPlanAsync(keep, ct).ConfigureAwait(false);

        // §9, stamped here for the reason the guard is: a store has to be protected, and a step that
        // cannot leave one withheld, on every plan, and no provider may be able to forget either. First,
        // so a file step whose subject is a store is withheld as the store it is rather than as a
        // recent file, and so the refusals asked about below are those of the steps that remain.
        var plan = MailStorePlan.Apply(built, measured);

        // A provider may hand back a plan already carrying a guard of its own, and the user's is
        // not allowed to loosen it. §5.3's floor under a scratch folder is the case: live working
        // files sit among dead ones and look identical there, so the cut-off is part of what makes
        // the location offerable at all rather than a preference about it. Stamping the user's
        // value over the top would have silently widened what the removal takes, on the one
        // provider where that is the documented mistake.
        //
        // Every other provider returns a plan with no guard of its own, so this is exactly the
        // user's value for all of them.
        var effective = MinimumAge.Stricter(plan.Keep, keep);
        var guarded = effective.IsOn ? Guarded(plan, effective, keep) : plan;

        // Stamped here for the reason the guard is: no provider held the defect, and a provider
        // cannot be allowed to forget the fix. After the guard, so what is asked again is exactly
        // what the guard leaves to the removal, and the subtraction is from the guarded figure.
        return RecordedRefusals.Apply(guarded, _refusals, WindowsFileSystem.Default, ct);
    }

    /// <summary>
    /// What this provider would remove. Called by <see cref="PlanAsync"/> and by nothing else.
    ///
    /// <paramref name="keep"/> is handed on to <see cref="PlanDeletionsAsync"/>, so the figures for
    /// paths Deguffer deletes itself exclude what the removal will not take. It is deliberately not
    /// handed to <see cref="MeasureAllAsync"/>, which measures a §5.1 command step's probe — see
    /// there for why that one is never guarded. A provider that measures nothing has nothing to do
    /// with it.
    /// </summary>
    protected abstract Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct);

    public Task<CleanupResult> ExecuteAsync(
        CleanupPlan plan,
        RunReach? runReach = null,
        RunResidue? residue = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.ProviderId != Id)
        {
            throw new ArgumentException(
                $"Plan belongs to provider '{plan.ProviderId}', not '{Id}'.", nameof(plan));
        }

        return _executor.ExecuteAsync(plan, runReach, residue, progress, ct);
    }

    public Task<VerificationResult> VerifyAsync(
        CleanupPlan plan,
        RunReach? runReach = null,
        CancellationToken ct = default) =>
        Task.FromResult(PlanVerifier.Verify(plan, runReach, residue: null, ct, Cloud));

    /// <summary>A plan with nothing to do, and the reason the user is shown.</summary>
    protected CleanupPlan EmptyPlan(string why) => new()
    {
        ProviderId = Id,
        ProviderName = Name,
        Tier = Tier,
        WhatHappensOnNextUse = WhatHappensOnNextUse,
        Notes = [new PlanNote(PlanNoteSeverity.Information, why)],
    };

    /// <summary>
    /// A plan with nothing to do because nothing was looked at, and the reason the user is shown.
    ///
    /// Distinct from <see cref="EmptyPlan"/> for the shell's sake rather than the executor's: both
    /// are empty and neither removes anything, but only one of them may be rendered as "Already
    /// clear". See <see cref="CleanupPlan.WasNotExamined"/>.
    /// </summary>
    protected CleanupPlan UnexaminedPlan(string why) => EmptyPlan(why) with { WasNotExamined = true };

    /// <summary>
    /// A plan with nothing to do because Windows would not say what is at <paramref name="root"/>,
    /// so the provider could not reach it to look.
    ///
    /// <para>A warning rather than information, and <see cref="CleanupPlan.HasUnreadableRoot"/>
    /// rather than <see cref="CleanupPlan.WasNotExamined"/>: the second of those is Deguffer's own
    /// decision not to look, and this is not a decision Deguffer made.</para>
    /// </summary>
    protected CleanupPlan UnreadableRootPlan(string root) => UnreadableRootPlan([root]);

    /// <summary>
    /// The same plan for a provider whose tool names several locations, every one of which Windows
    /// would not describe. Each is named, because they may sit on different drives and the reader
    /// has to be able to go and look at each.
    /// </summary>
    protected CleanupPlan UnreadableRootPlan(IReadOnlyList<string> roots) =>
        // Composed from EmptyPlan rather than repeating its skeleton, as UnexaminedPlan is: a field
        // added there has to reach every plan with nothing to do. The note is replaced rather than
        // appended, because EmptyPlan's is Information and this one is a warning.
        EmptyPlan(UnreadableRoot.WhyItCouldNotBeReached(roots[0])) with
        {
            Notes = [.. roots.Select(UnreadableRoot.UnreachedNote)],
            HasUnreadableRoot = true,
        };

    /// <summary>
    /// The plan a provider owes before it has looked at anything, or null where its root is there
    /// and it should carry on.
    ///
    /// <para><b>Written once because getting it wrong is silent.</b> A provider that reaches its
    /// cache by name has two ways of being told nothing is there, and only one of them means it.
    /// Collapsing them is how "Gradle is not installed for this user" came to be said about a
    /// directory holding a cache, and how <see cref="ICleanupProvider.IsPresentAsync"/> came to
    /// deny the row on the same evidence — so the user was shown nothing at all about the largest
    /// thing on the disk. See <see cref="Safety.PathPresence"/>.</para>
    /// </summary>
    /// <param name="root">The directory the provider reaches by name before it classifies anything.</param>
    /// <param name="absent">What to tell the user where the root is genuinely not there.</param>
    protected CleanupPlan? NothingToPlanFor(string root, string absent) => LongPath.ProbeDirectory(root) switch
    {
        PathPresence.Absent => EmptyPlan(absent),
        PathPresence.Refused => UnreadableRootPlan(root),
        _ => null,
    };

    /// <summary>
    /// The same for a provider whose tool names several locations: null where any of them is there,
    /// so the provider carries on and adds <see cref="ReachedDirectories.UnreachedNotes"/> for the
    /// rest, and otherwise the plan for none being there or none being described.
    /// </summary>
    /// <param name="found">What Windows said about each location the tool named.</param>
    /// <param name="absent">What to tell the user where every location is genuinely not there.</param>
    private protected CleanupPlan? NothingToPlanFor(ReachedDirectories found, string absent) =>
        found.Present.Count > 0 ? null
        : found.CouldNotBeReached ? UnreadableRootPlan(found.Refused)
        : EmptyPlan(absent);

    /// <summary>
    /// §5.6 — capture what each protected path was before the run, so verification can tell
    /// "survived" from "was never there", and from "is still standing and has been emptied".
    ///
    /// <para>A path Windows would not describe is recorded as <see cref="PathPresence.Refused"/>, never
    /// as absent, so a provider protects a folder it could not reach exactly as it protects one it
    /// could. See <see cref="ProtectedPath.PresenceBefore"/>.</para>
    ///
    /// <para>The content question stops at the first content it finds, so it costs a listing or two
    /// per protected path rather than a walk of everything the path holds (G4). See
    /// <see cref="DirectoryContent"/>.</para>
    /// </summary>
    protected static IReadOnlyList<ProtectedPath> Protect(params (string Path, string Reason)[] candidates) =>
    [
        .. candidates.Select(c => new ProtectedPath(
            c.Path,
            c.Reason,
            LongPath.ProbeEntry(c.Path),
            DirectoryContent.IsPresent(c.Path))),
    ];

    /// <summary>§5.3 warning for this provider's processes, or null if none are running.</summary>
    protected PlanNote? BuildRunningProcessNote() =>
        RunningProcessNotice.For(Inspector, ConflictingProcessNames);

    /// <summary>
    /// Measure the paths a §5.1 command step reports against, and produce the note that goes with
    /// them.
    ///
    /// <para>The note is not optional: §5.5 requires the fallback to be observable, and a slow scan
    /// is otherwise indistinguishable from a large directory — the user is never told that elevating
    /// would make it quick. Bundling it with the measurement is what stops a new provider silently
    /// losing that by forgetting a separate call.</para>
    ///
    /// <para><b>The guard is not an argument here, and that is the point.</b> §5.1 leaves a tool's
    /// own eviction command deciding what it removes, so a figure that withheld recent files would
    /// describe a deletion nobody is going to perform. Worse, it is the figure
    /// <see cref="PlanExecutor"/> subtracts an after-measure from to report what the command
    /// reclaimed — and that after-measure comes from
    /// <see cref="IDirectoryScanner.MeasureFromDiskAsync"/>, which is unguarded for the same reason.
    /// The two sides would then be measured on different bases: the reclaim would come out short,
    /// and where the command frees less than the guard withheld it would come out negative and
    /// report that the cache grew.</para>
    ///
    /// <para>Every provider call site is a command step's probe, so refusing the argument is what
    /// makes that unmistakable. The guarded measurement is <see cref="PlanDeletionsAsync"/>'s, and
    /// it is guarded because those paths are ones Deguffer deletes itself. The one age a command
    /// step may legitimately measure against is <em>its own</em> — see
    /// <see cref="MeasureAgedAsync"/>, which is not the user's guard.</para>
    /// </summary>
    protected Task<ScanBatch> MeasureAllAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        MeasureAllAsync(paths, MinimumAge.Off, ct);

    /// <summary>
    /// Measure paths a plan has undertaken <em>not</em> to delete, under the guard that plan is
    /// running with, so a figure can have them taken out of it.
    ///
    /// <para>The second guarded measurement in this class, and the reason it is a separate member
    /// rather than an argument on <see cref="MeasureAllAsync(IReadOnlyList{string}, CancellationToken)"/>
    /// is the paragraph there: that one is a §5.1 command step's probe and must never be guarded,
    /// because the tool decides for itself what it removes. This one is the opposite case. Its
    /// subject is a path Deguffer <em>would</em> have deleted and has chosen to spare — §5.3's
    /// entry that something is running out of — and the figure it produces is subtracted from a
    /// guarded estimate, so it has to be measured on the same basis or the subtraction is between
    /// two different quantities.</para>
    ///
    /// <para>Naming the two apart is what keeps a later reader from "simplifying" them into one
    /// method with a defaulted argument, which would make the dangerous call site the easy one.</para>
    /// </summary>
    protected Task<ScanBatch> MeasureSparedAsync(
        IReadOnlyList<string> paths,
        MinimumAge keep,
        CancellationToken ct) =>
        MeasureAllAsync(paths, keep, ct);

    /// <summary>
    /// A command step's probe measured twice: the whole of what those paths hold, and the part of it
    /// older than <paramref name="age"/>.
    ///
    /// <para><b>For the command that takes an age as a parameter.</b>
    /// <c>FhManagew.exe -cleanup &lt;days&gt;</c> is the one, and the age here is the number handed
    /// to it, never <see cref="MinimumAge"/>'s ordinary subject of the user's guard on recently
    /// changed files. The estimate has to be the aged part, because that is the only part the
    /// command will consider; the probe has to be the whole, because
    /// <see cref="PlanExecutor"/> subtracts an unguarded after-measure from it and the two sides
    /// must be measured on one basis.</para>
    ///
    /// <para><b>It is two passes over one tree, and there is no third figure to carry forward.</b>
    /// G4's rule against re-measuring assumes the second question has the same answer as the first.
    /// These are different quantities, and <see cref="IDirectoryScanner"/> answers one filter at a
    /// time — so the cost is real and stated here rather than hidden in a provider. §5.5's fast path
    /// serves both readings from one volume index where the table can be read, which is what keeps
    /// it affordable on the tens of gigabytes such a target holds.</para>
    ///
    /// <para>Here rather than in the provider so that every measurement a provider makes still goes
    /// through this class, and so the exception to the paragraph above is stated where the rule is.
    /// </para>
    /// </summary>
    protected async Task<(ScanBatch Whole, ScanBatch Aged)> MeasureAgedAsync(
        IReadOnlyList<string> paths,
        MinimumAge age,
        CancellationToken ct) =>
    (
        await MeasureAllAsync(paths, MinimumAge.Off, ct).ConfigureAwait(false),
        await MeasureAllAsync(paths, age, ct).ConfigureAwait(false)
    );

    private async Task<ScanBatch> MeasureAllAsync(
        IReadOnlyList<string> paths,
        MinimumAge keep,
        CancellationToken ct)
    {
        var sizes = new List<ScanSize>(paths.Count);
        var withheld = new List<bool>(paths.Count);
        var stores = new List<IReadOnlyList<string>>(paths.Count);
        var fallback = FallbackReason.None;

        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();

            var measured = await Scanner.MeasureAsync(path, keep, progress: null, ct).ConfigureAwait(false);
            sizes.Add(measured.Size);
            withheld.Add(measured.WithheldRecent);
            stores.Add(measured.MailStores);

            foreach (var store in measured.MailStores)
            {
                MeasuredMailStores.Value?.Add(store);
            }

            // Paths in one plan can sit on different volumes and so take different routes; the
            // first reason to appear is the one the user is shown.
            if (fallback == FallbackReason.None)
            {
                fallback = measured.Fallback;
            }
        }

        return new ScanBatch(sizes, fallback, withheld, stores);
    }

    /// <summary>
    /// Measure every target and turn it into the step that will delete it.
    ///
    /// The pairing of a target with its size is positional, so it lives here rather than being
    /// rewritten per provider: a loop that indexes two lists in step is exactly the shape that
    /// silently attributes one directory's size to another.
    /// </summary>
    protected async Task<(IReadOnlyList<CleanupStep> Steps, ScanBatch Measured)> PlanDeletionsAsync(
        IReadOnlyList<DeletionTarget> targets,
        MinimumAge keep,
        CancellationToken ct)
    {
        if (targets.FirstOrDefault(t => t.IndexedBy is { Count: > 0 } && t.Kind != TargetKind.Directory) is { Path: { } unindexable })
        {
            throw new ArgumentException($"Only a directory removed outright can carry an index: {unindexable}.", nameof(targets));
        }

        // An index is removed with the directory it indexes, so it is measured with it.
        var (each, measured) = await MeasureGroupsAsync(
            [.. targets.Select(t => (IReadOnlyList<string>)[t.Path, .. t.IndexedBy ?? []])], keep, ct).ConfigureAwait(false);

        var steps = new List<CleanupStep>(targets.Count);

        foreach (var (target, group) in targets.Zip(each))
        {
            DeleteStep step = target.Kind switch
            {
                TargetKind.File => new DeleteFileStep(target.Path, target.Reason),
                TargetKind.RecycleBin => new EmptyRecycleBinStep(target.Path, target.Reason),
                TargetKind.DirectoryContents => new ClearDirectoryStep(target.Path, target.Reason),
                _ => new DeleteDirectoryStep(target.Path, target.Reason) { IndexedBy = target.IndexedBy ?? [] },
            };

            steps.Add(step with
            {
                Estimated = target.Kind is TargetKind.DirectoryContents or TargetKind.RecycleBin
                    ? WithoutItsOwnEntry(group.Size)
                    : group.Size,
                LastWritten = target.LastWritten,
                RequiresElevation = target.RequiresElevation,
                WithheldRecent = group.WithheldRecent,
                MailStores = group.MailStores,
                Identity = target.Identity,
                IsLeftover = target.IsLeftover,
                Facets = target.Facets ?? [],
                Group = target.Group,
                UseCheck = target.UseCheck,
            });
        }

        return (steps, measured);
    }

    /// <summary>
    /// Measure every directory each Disk Cleanup handler would clear, and turn each handler into the
    /// step that will run it.
    ///
    /// <para><b>Measured under the guard, although the handler cannot honour it.</b> What the guard
    /// is needed for here is the answer, not the figure: a handler clears its directories whole, so
    /// a step whose measurement held anything back would take what the user asked to keep, and
    /// <see cref="Guarded"/> withdraws it. Where nothing is held back the two measurements are the
    /// same number.</para>
    ///
    /// <para>Here rather than in the provider for the reason <see cref="PlanDeletionsAsync"/> is: one
    /// step's figure is the sum of several paths, and the pairing of sizes with paths is positional.
    /// </para>
    /// </summary>
    protected async Task<(IReadOnlyList<CleanupStep> Steps, ScanBatch Measured)> PlanDiskCleanupsAsync(
        IReadOnlyList<DiskCleanupTarget> targets,
        MinimumAge keep,
        CancellationToken ct)
    {
        var (each, measured) = await MeasureGroupsAsync(
            [.. targets.Select(t => (IReadOnlyList<string>)[t.Path, .. t.AlsoClears])], keep, ct).ConfigureAwait(false);

        var steps = new List<CleanupStep>(targets.Count);

        foreach (var (target, group) in targets.Zip(each))
        {
            steps.Add(new DiskCleanupStep(target.Path, target.Reason)
            {
                Handler = target.Handler,
                Volume = target.Volume,
                AlsoClears = target.AlsoClears,
                Estimated = group.Size,
                LastWritten = target.LastWritten,
                RequiresElevation = target.RequiresElevation,
                WithheldRecent = group.WithheldRecent,
                MailStores = group.MailStores,
            });
        }

        return (steps, measured);
    }

    /// <summary>
    /// Measure each group of paths one step destroys, and add each group up.
    ///
    /// <para>The one place a measurement is paired with the paths it measured. The batch is flat and the
    /// pairing is positional, which is exactly the shape that silently attributes one directory's size
    /// to another, so every step that is the sum of several paths comes through here.</para>
    /// </summary>
    private async Task<(IReadOnlyList<GroupMeasure> Each, ScanBatch Measured)> MeasureGroupsAsync(
        IReadOnlyList<IReadOnlyList<string>> groups,
        MinimumAge keep,
        CancellationToken ct)
    {
        var measured = await MeasureAllAsync([.. groups.SelectMany(g => g)], keep, ct).ConfigureAwait(false);

        var each = new List<GroupMeasure>(groups.Count);
        var next = 0;

        foreach (var group in groups)
        {
            var span = Enumerable.Range(next, group.Count).ToList();
            next += group.Count;

            each.Add(new GroupMeasure(
                span.Aggregate(ScanSize.Zero, (total, i) => total + measured.Sizes[i]),
                span.Any(i => measured.WithheldRecent[i]),
                [.. span.SelectMany(i => measured.MailStores[i]).Distinct(StringComparer.OrdinalIgnoreCase)]));
        }

        return (each, measured);
    }

    /// <summary>What one step's paths hold together, as <see cref="MeasureGroupsAsync"/> adds them up.</summary>
    private readonly record struct GroupMeasure(ScanSize Size, bool WithheldRecent, IReadOnlyList<string> MailStores);

    /// <summary>
    /// A measurement of a folder emptied in place, which stays standing, so its own entry is not
    /// among what the step takes.
    ///
    /// <para>The measurement counted that entry only where nothing inside the folder stays. Where
    /// something does, this counts one short — the direction a count deciding whether anything is
    /// offered may be wrong in.</para>
    /// </summary>
    private static ScanSize WithoutItsOwnEntry(ScanSize size) =>
        size with { Entries = Math.Max(0, size.Entries - 1) };

    /// <summary>
    /// The guard applied to a finished plan: carried onto it, said out loud, and — for a step whose
    /// whole subject is one recent file — acted on by withdrawing the offer.
    ///
    /// <para>A directory needs no withdrawing. Its recent files are already out of the estimate,
    /// and <see cref="DirectoryRemover"/> leaves them where they are, so the step stays and does
    /// less. A <see cref="DeleteFileStep"/> has nothing left to do once its one file is protected,
    /// and offering a row that will reclaim nothing is worse than not offering it — so it is
    /// withdrawn, and §5.6 is told to prove the file is still there afterwards.</para>
    ///
    /// <para><b>That reasoning holds because every deletion here honours the guard itself, and
    /// <see cref="EmptyRecycleBinStep"/> cannot.</b> Windows empties a bin whole, so such a step
    /// under a guard would stay and do <em>more</em> rather than less. It never arrives here in
    /// that state — <see cref="Providers.RecycleBinProvider"/> takes the direct route whenever the
    /// guard is on — and <see cref="PlanExecutor"/> refuses the pairing outright rather than
    /// leaving that to one expression in one provider.</para>
    ///
    /// <para><b>A <see cref="DiskCleanupStep"/> is withdrawn where the guard would hold anything
    /// back.</b> Windows clears its directories whole and there is no direct route to fall back on,
    /// because the handler is chosen for what it does besides deleting. Where the guard holds nothing
    /// back it is satisfied, and the step stays.</para>
    ///
    /// <para><b>So is a directory that goes with its index</b>, for the reason that step gives: see
    /// <see cref="DeleteDirectoryStep.IndexedBy"/>.</para>
    /// </summary>
    /// <param name="keep">
    /// The guard actually in force, which is the stricter of the user's and the provider's own.
    /// </param>
    /// <param name="asked">
    /// The user's own guard, for the sentence alone. The two differ only where a provider carries a
    /// floor of its own, and the note below says "as you asked" — which would be untrue of a cut-off
    /// the provider imposed and the user never chose. A provider with a floor explains it itself,
    /// where the reasoning for that particular location is.
    /// </param>
    private static CleanupPlan Guarded(CleanupPlan plan, MinimumAge keep, MinimumAge asked)
    {
        var files = plan.Steps
            .OfType<DeleteFileStep>()
            .Where(step => keep.ProtectsFile(step.Path))
            .ToList();

        // Windows clears a handler's directories whole, so one holding anything the guard would keep
        // cannot do less: it would do all of it. See PlanDiskCleanupsAsync.
        var whole = plan.Steps
            .OfType<DiskCleanupStep>()
            .Where(step => step.WithheldRecent)
            .ToList();

        // A directory goes only once its whole index has, and a manifest the guard kept would name an
        // output nobody could then promise is there. See DeleteDirectoryStep.IndexedBy.
        var indexed = plan.Steps
            .OfType<DeleteDirectoryStep>()
            .Where(step => step.IndexedBy.Count > 0 && step.WithheldRecent)
            .ToList();

        var notes = new List<PlanNote>(plan.Notes);

        notes.AddRange(whole.Select(step => new PlanNote(
            PlanNoteSeverity.Information,
            $"Leaving {LongPath.Display(step.Path)} alone: Windows clears it whole, and something in it "
            + $"{keep.DescribeChange()}.")));

        notes.AddRange(indexed.Select(step => new PlanNote(
            PlanNoteSeverity.Information,
            $"Leaving {LongPath.Display(step.Path)} and its index alone: they go together and whole or not "
            + $"at all, and something in them {keep.DescribeChange()}.")));

        // Only where there is something to say it about. Every plan comes through here, including
        // the empty one a provider returns for a toolchain that is not installed — and "the sizes
        // here already exclude those files" under "Go is not installed on this machine" describes
        // sizes that do not exist, on the majority of rows on an ordinary machine.
        if (plan.Steps.Count > 0 && asked.IsOn)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"Leaving anything changed in the last {asked.Describe()} alone, as you asked. The "
                + "sizes here already exclude those files."));
        }

        // §5.1 keeps a tool's own eviction command as the preferred route, and that command decides
        // for itself what it removes. Saying so is the whole of what can be done about it: the
        // alternative is to stop using the command while the guard is on, which would replace the
        // tool's knowledge of its own cache with ours — the exact substitution §5.2 exists to
        // refuse. NuGet's own clear reached two locations that were not under .nuget at all.
        //
        // Said about the user's own guard, like the note above it: a floor a provider imposes is
        // that provider's to explain, and no provider carries both a floor and a command step.
        if (asked.IsOn && plan.Steps.OfType<RunCommandStep>().Any())
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Warning,
                $"{plan.ProviderName} is cleared by running its own tool, and that tool decides what "
                + "it removes. Recent files are not protected from it."));
        }

        return plan with
        {
            Keep = keep,
            Steps = [.. plan.Steps.Except(files).Except(whole).Except(indexed)],
            Notes = notes,
            ProtectedPaths =
            [
                .. plan.ProtectedPaths,
                .. files.Select(step => (Path: step.Path, Reason: $"Left alone because it {keep.DescribeChange()}."))
                    .Concat(whole.SelectMany(step => step.Destroys).Select(path => (Path: path, Reason:
                        $"Left alone because Windows clears it whole, and something it would clear {keep.DescribeChange()}.")))
                    .Concat(indexed.SelectMany(step => step.Destroys).Select(path => (Path: path, Reason:
                        $"Left alone because it goes whole with its index or not at all, and something there {keep.DescribeChange()}.")))
                    .Select(withheld => new ProtectedPath(
                        withheld.Path,
                        withheld.Reason,
                        // Measured during planning, so it was there when the plan was made — the same
                        // claim, and the same reasoning, as CleanupPlan.NarrowedTo makes for a step the
                        // user declined.
                        PresenceBefore: PathPresence.Present,

                        // The row's zero now excludes a real file, and this is the only place left on
                        // the plan to say so. See CleanupPlan.HasRecentContentHeldBack.
                        Withheld: Withholding.TooRecent)),
            ],
        };
    }
}
