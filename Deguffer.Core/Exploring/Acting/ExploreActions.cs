using System.Collections.Concurrent;
using Deguffer.Core.Diagnostics;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Exploring.Acting;

/// <summary>
/// Asks about an Explore removal and carries it out.
///
/// <para>Separate from the Explore page's view-models because the subjects differ. They are about
/// which node is being looked at and what the screen says about it; this is about what happens to a
/// thing the user picked. Keeping them apart is G1 applied to a page that would otherwise be
/// scanning, navigating, formatting <em>and</em> deleting.</para>
///
/// <para>In Core because what it holds are rules about what gets deleted: what a path is told
/// while the policy is still being built, that a failed build refuses everything, that nothing is
/// asked about what the policy refuses or the Recycle Bin cannot take, and that a removal waits for
/// the whole policy rather than acting on part of it. What may be removed is still
/// <see cref="ExploreActionPolicy"/>'s, what the user is told is <see cref="ExploreRemovalPrompt"/>'s,
/// and what happened is <see cref="ExploreRemovalReport.Summary"/>'s.</para>
///
/// <para>What the page is told about a path is kept for the life of the policy that answered it,
/// because each answer asks the machine where the path's volume is mounted, and a page selects,
/// lists and restates the same paths many times over. What a removal acts on is never read from
/// that store: <see cref="RemoveAsync"/> asks the policy again, at the moment it acts.</para>
/// </summary>
public sealed class ExploreActions
{
    /// <summary>
    /// What Explore says about a path while it is still working out what it must protect.
    ///
    /// <para>A refusal rather than <see cref="ExploreVerdict.Unclassified"/>, because half of §5.2
    /// is not known until the providers have been asked: a moved Go workspace, a conda prefix, a
    /// staging folder an installer is using. Answering "unclassified" in the meantime would allow
    /// exactly the paths this policy exists to refuse, for however long the probes take — and the
    /// user cannot tell a fast answer from a complete one.</para>
    ///
    /// <para>It says when, because a policy built for a scan finishes once the scan has
    /// (<see cref="Reconsider"/>), and "in a moment" is untrue of a scan that takes ten.</para>
    /// </summary>
    private static readonly ExploreVerdict Working = ExploreVerdict.Refuse(
        "Deguffer is still working out what it has to protect on this machine, and finishes once any "
        + "scan under way has ended. This will say what it is then.");

    /// <summary>
    /// What Explore says about every path once a build has failed, until one succeeds.
    ///
    /// <para>A refusal for the reason <see cref="Working"/> is one: without the probed half there is
    /// no knowing what else would have been refused. It says what happened rather than that an answer
    /// is on its way, because nothing is on its way until the next scan asks again.</para>
    /// </summary>
    private static readonly ExploreVerdict Failed = ExploreVerdict.Refuse(
        "Deguffer could not work out what it has to protect on this machine, so Explore removes "
        + "nothing for now. Scanning again tries once more.");

    private readonly Func<ProtectionScope, CancellationToken, Task<ExploreActionPolicy>> _build;
    private readonly Func<IExploreConfirmationPrompt> _prompt;
    private readonly CrashLog _faults;
    private readonly RunningActions _running;
    private readonly IRecycleBin? _recycleBin;

    /// <summary>
    /// How many paths <see cref="NoteAsync"/> asks about at once (G4). Measured rather than derived
    /// from the processor count: each question waits on the volume manager, and a folder of a few
    /// thousand entries is answered no faster by more than four.
    /// </summary>
    private const int Asking = 4;

    private PolicyBuild? _current;

    /// <param name="build">
    /// How to assemble the policy for this machine, for what it will be asked about. A factory rather
    /// than a built policy, because <see cref="Reconsider"/> has to be able to ask for a fresh one: a
    /// declaration that a path is in use is true of this minute and not of the next.
    /// </param>
    /// <param name="faults">
    /// Where a failed build is written down. Handed in rather than reached for, because the app's
    /// own log is rooted in the real <c>%LOCALAPPDATA%</c>, and a test of a failed build would
    /// otherwise write into the developer's profile.
    /// </param>
    /// <param name="running">
    /// What is changing the machine on every page. A removal is recorded in it from the moment it is
    /// confirmed until its §5.6 verification has reported, so nothing ends the process under it.
    /// </param>
    /// <param name="recycleBin">Handed to <see cref="ExploreRemover"/>; the shell's own bin where null.</param>
    public ExploreActions(
        Func<ProtectionScope, CancellationToken, Task<ExploreActionPolicy>> build,
        Func<IExploreConfirmationPrompt> prompt,
        CrashLog faults,
        RunningActions running,
        IRecycleBin? recycleBin = null)
    {
        _build = build;
        _prompt = prompt;
        _faults = faults;
        _running = running;
        _recycleBin = recycleBin;
    }

    /// <summary>
    /// Raised on the thread that started a build once that build has finished, whether it succeeded
    /// or failed, so a page that stated <see cref="Working"/> can ask again.
    /// </summary>
    public event EventHandler? Ready;

    /// <summary>
    /// Actions over a policy for this machine, which is built in the background when the page is
    /// constructed and again at each scan.
    ///
    /// <para>In the background because building it constructs every provider and runs their probes,
    /// which would otherwise hold up the page. Never skipped, because §5.2 is read out of those
    /// providers: a policy assembled without them would refuse the operating system's directories
    /// and let a tool's credentials through.</para>
    ///
    /// <para>Each build looks at the machine afresh. The providers are constructed again, so what
    /// each of them resolved goes with the old ones, and they are given their own
    /// <see cref="UserEnvironment"/> and <see cref="LiveTreeInspector"/>, so where each command is on
    /// <c>PATH</c> and what is running are read at the build rather than kept from an earlier look.
    /// The shared instances are not cleared instead, because that would change what a Storage pass
    /// already under way sees.</para>
    /// </summary>
    public static ExploreActions ForThisMachine(
        Func<IExploreConfirmationPrompt> prompt, CrashLog faults, RunningActions running) =>
        new(
            async (scope, ct) => (await MachineProtections.ForThisMachineAsync(scope, ct).ConfigureAwait(false)).Policy,
            prompt,
            faults,
            running);

    /// <summary>
    /// Start building the policy, unless one is built or on its way. Called once, when the page is
    /// constructed, so the first selection has an answer waiting rather than a refusal that has to be
    /// taken back. The page is kept while the app runs, so a return visit keeps the policy it had, and
    /// a scan builds a new one.
    /// </summary>
    public void Prepare()
    {
        if (_current?.Policy is null or { IsFaulted: true } or { IsCanceled: true })
        {
            Start(ProtectionScope.Everything);
        }
    }

    /// <summary>
    /// Build the policy again, replacing the one there is.
    ///
    /// <para>What is running changes, and a policy holds the answer it was given: a staging folder
    /// an installer had open when the page opened stays refused after the install finishes, and an
    /// entry a program started using since then is allowed. Neither is acceptable indefinitely, and
    /// a scan is the natural moment to ask again — it is the point at which everything else on the
    /// page is being re-measured, and it takes long enough that the probes cost nothing beside
    /// it.</para>
    ///
    /// <para><b>Scoped to the scan</b>, whose tree is what the page will then show. What the scan
    /// listed is read from its tree rather than listed again beside it, and what it cannot reach is
    /// not looked in, so the policy is complete once the scan ends rather than before. Removal waits
    /// for the scan anyway, so nothing that could be removed is answered later for it.</para>
    /// </summary>
    /// <param name="scope">What the scan will find, given once it ends (<see cref="ProtectionScope.Scanned"/>).</param>
    public void Reconsider(ProtectionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        Start(scope);
    }

    /// <summary>
    /// Whether Explore will remove this, and what to say either way (§7.1): <see cref="Working"/>
    /// while the policy is being built, and <see cref="Failed"/> once a build has failed. The shell
    /// is told through <see cref="Ready"/> when that changes.
    ///
    /// <para>It starts nothing. The page opening, a scan and a removal each start a build, and a
    /// question that restarted a failed one would run every provider's probes again each time the
    /// selection changed.</para>
    ///
    /// <para>A path is asked about once for each build, and the answer is kept until the next build
    /// replaces it. Safe to call from any thread, because the Files view lists its rows off the
    /// page's.</para>
    /// </summary>
    public ExploreVerdict Verdict(string path)
    {
        var build = _current;

        return Unbuilt(build) ?? build!.Ask(path);
    }

    /// <summary>
    /// The sentence <see cref="ExploreRefusalNote"/> gives for <paramref name="items"/>, where it can
    /// be given without asking the machine: while the policy is being built or has failed, or where
    /// the current build has already answered for every item. False otherwise, and then
    /// <see cref="NoteAsync"/> answers.
    ///
    /// <para>Separate from <see cref="NoteAsync"/> so a page can say at once what it already knows.
    /// A selection carried through a scan, or picked again, is answered on the spot rather than
    /// cleared and restated a moment later.</para>
    /// </summary>
    public bool TryNote(IReadOnlyList<ExploreItem> items, out string? note)
    {
        ArgumentNullException.ThrowIfNull(items);

        // Read once, so every item is answered by the same build even if one finishes meanwhile.
        var build = _current;

        if (Unbuilt(build) is { } standing)
        {
            note = ExploreRefusalNote.For(items, _ => standing);
            return true;
        }

        if (!items.All(item => build!.Knows(item.Path)))
        {
            note = null;
            return false;
        }

        note = ExploreRefusalNote.For(items, build!.Ask);
        return true;
    }

    /// <summary>
    /// The sentence <see cref="ExploreRefusalNote"/> gives for <paramref name="items"/>, worked out
    /// off the calling thread.
    ///
    /// <para>Off the thread because each path costs a question to the volume manager, and a list
    /// selects any number of rows at once: a few thousand entries took seconds when they were asked
    /// on the page's thread. Every item is answered by the build current when this is called, so a
    /// build that finishes or is replaced meanwhile cannot leave half the sentence about each.</para>
    /// </summary>
    public Task<string?> NoteAsync(IReadOnlyList<ExploreItem> items, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(items);

        var build = _current;

        if (Unbuilt(build) is { } standing)
        {
            return Task.FromResult(ExploreRefusalNote.For(items, _ => standing));
        }

        return Task.Run(
            () =>
            {
                Parallel.ForEach(
                    items,
                    new ParallelOptions { MaxDegreeOfParallelism = Asking, CancellationToken = ct },
                    item => build!.Ask(item.Path));

                return ExploreRefusalNote.For(items, build!.Ask);
            },
            ct);
    }

    /// <summary>
    /// What every path is told where <paramref name="build"/> cannot answer for it:
    /// <see cref="Working"/> while it is being built, and <see cref="Failed"/> once it has failed.
    /// Null where it has been built.
    /// </summary>
    private static ExploreVerdict? Unbuilt(PolicyBuild? build) => build?.Policy switch
    {
        { IsCompletedSuccessfully: true } => null,
        { IsFaulted: true } or { IsCanceled: true } => Failed,
        _ => Working,
    };

    /// <summary>
    /// Ask, then remove. Null when the user declined, which is a decision rather than a failure.
    ///
    /// <para>Nothing is asked about an item the policy refuses, or, on the way to the Recycle Bin,
    /// one the bin cannot take. A dialog covering something that is then refused teaches the user
    /// that saying yes is how you find out what happens, and §7.1 wants the reason stated instead —
    /// so a wholly refused selection goes straight to a report carrying the reasons.</para>
    ///
    /// <para>It waits for the policy rather than reading whatever is ready, because this is the path
    /// that deletes. A removal decided against a half-built policy is the one thing the deferral
    /// above must never buy, and one whose build failed refuses everything it was given.</para>
    /// </summary>
    /// <param name="acting">
    /// Told what the removal will act on, which is the part of <paramref name="items"/> the policy
    /// allows and, on the way to the Recycle Bin, the bin can take, once both have said and before
    /// anything is asked: for a screen marking what is about to go, which must never mark what is
    /// refused (§7.1). Not told at all where the policy could not be built, because then nothing is
    /// acted on.
    /// </param>
    public async Task<ExploreRemovalReport?> RemoveAsync(
        IReadOnlyList<ExploreItem> items,
        ExploreRemovalMode mode,
        Action<IReadOnlyList<ExploreItem>>? acting = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count == 0)
        {
            return null;
        }

        // Everything, where the last build failed: the selection may be from any tree the page showed.
        var building = _current?.Policy is { IsFaulted: false, IsCanceled: false } current ? current : Start(ProtectionScope.Everything);

        // Waited for rather than awaited, so a build that fails reaches the user as a refusal of
        // what they picked rather than as an exception out of the command that deletes.
        await Task.WhenAny(building).ConfigureAwait(true);

        if (!building.IsCompletedSuccessfully)
        {
            return new ExploreRemovalReport(
                mode,
                [.. items.Select(item => new ExploreItemOutcome(item.Path, Removed: false, Bytes: 0, Failed.Reason))],
                new VerificationResult());
        }

        // Asked again rather than read from what the page was told, because this is the path that
        // deletes, and a volume mounted since an answer was kept has changed what that answer is.
        var policy = building.Result;
        var (allowed, _) = ExploreRemover.Partition(items, policy);

        // What the bin cannot take is treated as the policy's refusals are: not asked about, and
        // reported with why. The prompt promises the bin, and a yes to a move that never happens is
        // the lesson the policy's refusals are kept out of the prompt to avoid teaching. A folder is
        // answered by a walk of everything inside it, which is why this is off the page's thread, and
        // it is asked once per removal rather than at each change of selection.
        IReadOnlyList<ExploreItemOutcome> binRefused = mode == ExploreRemovalMode.RecycleBin && allowed.Count > 0
            ? await Task.Run(() => ExploreRemover.WhatTheBinCannotTake(allowed, _recycleBin, ct), ct).ConfigureAwait(true)
            : [];

        HashSet<string> notGoing = [.. binRefused.Select(refusal => refusal.Path)];
        IReadOnlyList<ExploreItem> going = notGoing.Count == 0 ? allowed : [.. allowed.Where(item => !notGoing.Contains(item.Path))];

        acting?.Invoke(going);

        if (going.Count > 0 &&
            !await _prompt().AskAsync(ExploreRemovalPrompt.For(mode, going), ct).ConfigureAwait(true))
        {
            return null;
        }

        // After the question rather than before it: until it is answered nothing has changed, and a
        // window closed over the dialog loses nothing.
        using var running = _running.Begin(RunningAction.ExploreRemoval);

        // Everything else goes back in, the policy's refusals included: the remover partitions again
        // and reports what it would not take, so the user is told about each one rather than seeing
        // it silently dropped from the count. What the bin refused is handed over as refused, so it
        // stays out even if the bin would take it by now, because the user was not asked about it,
        // and §5.6 still asserts it survived. Partition normalises the same path the same way, so it
        // is matched by the form the refusal carries.
        return await ExploreRemover.RemoveAsync(
                notGoing.Count == 0 ? items : [.. items.Where(item => LongPath.Entry(item.Path) is not { } path || !notGoing.Contains(path))],
                mode,
                policy,
                _recycleBin,
                refusedBeforeAsking: binRefused,
                ct: ct)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// Build a new policy and keep it, in place of any other.
    ///
    /// <para>No cancellation is passed. The policy belongs to the page rather than to the scan that
    /// happened to start it, and a build cancelled with the scan would answer every later question
    /// with <see cref="Failed"/>.</para>
    /// </summary>
    private Task<ExploreActionPolicy> Start(ProtectionScope scope)
    {
        var building = BuildAsync(scope);

        _current = new PolicyBuild(building);
        _ = AnnounceAsync(building);

        return building;
    }

    /// <summary>
    /// The factory's task, with a factory that throws before it returns one turned into a failed
    /// build. What it does before its first await, constructing every provider, would otherwise
    /// throw out of whichever caller started it, the page's own constructor among them.
    /// </summary>
    private async Task<ExploreActionPolicy> BuildAsync(ProtectionScope scope) =>
        await _build(scope, CancellationToken.None).ConfigureAwait(false);

    /// <summary>
    /// Raise <see cref="Ready"/> once <paramref name="building"/> has finished, on the thread that
    /// started it.
    ///
    /// <para>After the build's task has completed, never from inside the build. A handler raised
    /// before completion asks <see cref="Verdict"/> about a task that has not finished, is told
    /// <see cref="Working"/> again, and is never told otherwise.</para>
    ///
    /// <para>Only for the build still kept. One replaced while it ran has nothing to tell a page that
    /// is now waiting on its successor.</para>
    /// </summary>
    private async Task AnnounceAsync(Task<ExploreActionPolicy> building)
    {
        await Task.WhenAny(building).ConfigureAwait(true);

        // Recorded here because the page cannot show it: the refusal says a build failed, and the
        // log says which provider's probe failed and how. Reading the exception observes it, so it is
        // not reported a second time when the task is collected.
        if (building.Exception is { } failure)
        {
            _faults.Record("Building Explore's removal policy", failure);
        }

        if (ReferenceEquals(building, _current?.Policy))
        {
            Ready?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// One build of the policy, with what it has said about each path the page asked about. Kept
    /// together so a new build starts knowing nothing, and an answer is never read against a policy
    /// other than the one that gave it.
    /// </summary>
    private sealed class PolicyBuild(Task<ExploreActionPolicy> policy)
    {
        // By the path's exact text: a folder can be case-sensitive, and two names differing only in
        // case are then two things with two answers.
        private readonly ConcurrentDictionary<string, ExploreVerdict> _verdicts = new(StringComparer.Ordinal);

        public Task<ExploreActionPolicy> Policy { get; } = policy;

        /// <summary>Whether <paramref name="path"/> has been answered. Only of a build that has finished.</summary>
        public bool Knows(string path) => _verdicts.ContainsKey(path);

        /// <summary>What the policy says about <paramref name="path"/>, asked once. Only of a build that has finished.</summary>
        public ExploreVerdict Ask(string path) =>
            _verdicts.GetOrAdd(path, static (path, policy) => policy.MayRemove(path), Policy.Result);
    }
}
