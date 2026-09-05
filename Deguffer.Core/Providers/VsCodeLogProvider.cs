using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// What a Code - OSS editor wrote down about itself: one log folder per editor session, and the
/// crash reporter's database (0.29 GB on the measured machine, across 65 sessions).
///
/// <para><b>Tier 3, and separate from <see cref="VsCodeCacheProvider"/> for that reason alone.</b>
/// A plan carries its provider's tier, so a Tier 3 child declared inside a Tier 1 provider would be
/// pre-selected and removed under a Tier 1 sentence. §3's Tier 1 requires that whatever produced the
/// content re-creates it on demand; nothing re-creates a log of a session that has ended, or the
/// dump of a crash that will not happen again to order. The precedent is
/// <see cref="CrashDumpProvider"/> and <see cref="WindowsServicingLogProvider"/>, which are Tier 3
/// against exactly the same argument.</para>
///
/// <para><b>Why it is worth offering at all.</b> The editor writes a new <c>logs</c> folder every
/// time it starts and removes none of them, and every extension's output channel is written into
/// it. On the measured machine that was 141.7 MB across 65 sessions with the oldest three days old,
/// which is the shape of something that grows without bound rather than something that is kept.
/// <c>Crashpad</c> is the same fact with a different author: the crash reporter keeps its database
/// for as long as the editor is installed.</para>
///
/// <para><b>No age filter, on <see cref="CrashDumpProvider"/>'s reasoning.</b> A log written this
/// morning may be the only evidence in a bug report somebody is still writing. The answer to that is
/// the tier and the confirmation it requires, not a cut-off: Tier 3 leaves the row unselected and
/// says plainly that the loss is permanent, and the decision stays the user's. The guard on recently
/// changed files is theirs to set on top of that, and it protects the session that is running now
/// without anything here having to guess which one that is.</para>
///
/// <para>§5.1 does not apply: the editor has no command that clears either of these.</para>
/// </summary>
public sealed class VsCodeLogProvider : CleanupProviderBase
{
    /// <summary>
    /// The two children of the user-data folder that are a record rather than a cache. Everything
    /// else in there is Tier 4 to this provider — including the caches
    /// <see cref="VsCodeCacheProvider"/> removes, which are that provider's to offer under its own
    /// tier and its own sentence.
    /// </summary>
    public static readonly DisposableChildSet FolderChildren = new(
    [
        new ChildClassification(
            "logs",
            SafetyTier.UserData,
            "One folder for every time the editor has started, holding what it and every extension "
            + "wrote to their output channels. Nothing re-creates the log of a session that has "
            + "already ended."),
        new ChildClassification(
            "Crashpad",
            SafetyTier.UserData,
            "The crash reporter's database: the dump and the metadata for every time the editor "
            + "stopped unexpectedly. Each one is the record of a single failure, and nothing "
            + "re-creates it."),
    ]);

    private readonly VsCodeUserDataDiscovery _discovery;
    private IReadOnlyList<VsCodeUserData>? _editors;
    private IReadOnlyList<ToolRoot>? _toolRoots;

    public VsCodeLogProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
        => _discovery = new VsCodeUserDataDiscovery(Environment);

    public override string Id => "vscode-logs";

    public override string Name => "VS Code editor logs and crash reports";

    public override SafetyTier Tier => SafetyTier.UserData;

    public override string WhatHappensOnNextUse =>
        "The record of every past editor session and every crash it reported is destroyed, so none " +
        "of it can be attached to a bug report afterwards. The editor keeps writing new logs " +
        "exactly as before, and nothing about how it works changes.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Visual Studio Code, and the editors built on the same Code - OSS base — "
            + "VS Code Insiders, VSCodium, Cursor and the rest",
        Publisher = "Microsoft publishes Visual Studio Code; each derivative is published by its "
            + "own vendor",
        Purpose = "The editor writes a new log folder every single time it starts, holding what it "
            + "and every installed extension reported, and keeps a crash database beside it. "
            + "Neither is ever pruned, so both grow for as long as the editor is installed.",
        Recommendation = "This is a record of what happened, not a cache: nothing re-creates it. "
            + "Clear it once you are sure you are not in the middle of diagnosing something, and "
            + "keep in mind that an extension author asking for a log means one of these.",
    };

    /// <summary>
    /// The editors whose folders hold at least one of the two, memoised for the life of a planning
    /// pass (G4). Presence and planning ask the same question of the same disk.
    /// </summary>
    private IReadOnlyList<VsCodeUserData> Editors(CancellationToken ct = default) =>
        _editors ??= [.. _discovery.Discover(ct).Where(HasRecord)];

    /// <summary>
    /// §5.2 as §7.1 needs it read from outside. Declared from every folder discovered rather than
    /// from <see cref="Editors"/>: a folder with no log in it yet still holds the whole
    /// <c>User</c> tree, and that is the reason this declaration exists.
    ///
    /// <para>The same path is declared by <see cref="VsCodeCacheProvider"/> and by
    /// <see cref="ChromiumCacheProvider"/> as well. §7.1 reads the union of every declaration
    /// covering a path, so each provider states only the children it knows about.</para>
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
        _toolRoots ??=
        [
            .. _discovery.Discover().Select(editor => ToolRoot.Of(
                editor.Path,
                $"This is {editor.Name}'s own folder. Deguffer removes the logs and crash reports in "
                + "there from the Storage page, where it knows which of them are records — your "
                + "settings, your workspace state and your local file history sit beside them.",
                FolderChildren)),
        ];

    public override void InvalidateCaches()
    {
        _editors = null;
        _toolRoots = null;
        base.InvalidateCaches();
    }

    /// <summary>
    /// Presence is a log or a crash database actually on disk. An editor installed and never run
    /// keeps a user-data folder with neither in it.
    ///
    /// <para>A refused application-data root counts as present, on
    /// <see cref="ChromiumCacheProvider"/>'s reasoning: presence here is decided by enumerating, so
    /// a refusal would otherwise render the row as "Not installed" — a claim about a folder Deguffer
    /// never read.</para>
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(Editors(ct).Count > 0 || _discovery.UnreadableRoots.Count > 0);

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var editors = Editors(ct);

        if (editors.Count == 0)
        {
            return _discovery.UnreadableRoots.Count == 0
                ? EmptyPlan("No editor on this machine has written a log or a crash report.")
                : EmptyPlan(UnreadableRoot.WhyNothingWasPlanned(_discovery.UnreadableRoots[0]))
                    with { HasUnreadableRoot = true };
        }

        var notes = new List<PlanNote>();
        var targets = new List<DeletionTarget>();
        var declined = new List<(string Path, string Reason)>();
        var survivors = new List<(string Path, string Reason)>();

        var unreadable = _discovery.UnreadableRoots.Count > 0;

        foreach (var root in _discovery.UnreadableRoots)
        {
            notes.Add(UnreadableRoot.Note(root));
        }

        foreach (var editor in editors)
        {
            ct.ThrowIfCancellationRequested();

            survivors.Add((
                editor.Path,
                $"The '{editor.Name}' folder itself must survive — only its logs and crash reports "
                + "are removed."));
            survivors.AddRange(VsCodeUserDataDiscovery.NeverOffered
                .Select(n => (Path.Combine(editor.Path, n.RelativePath), n.Reason)));

            var outcome = CacheLevelWalk.Collect(
                editor.Path, [new CacheLevel(string.Empty, FolderChildren)],
                targets, declined, survivors, notes, ct);

            unreadable |= outcome.Unreadable;

            if (outcome.Spared > 0)
            {
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"In '{editor.Name}', {outcome.Spared} other "
                    + $"{(outcome.Spared == 1 ? "item is" : "items are")} left alone beside the logs. "
                    + "The editor's caches are offered separately, and your settings, workspace state "
                    + "and local file history are never offered at all."));
            }
        }

        if (targets.Count == 0 && declined.Count == 0 && !unreadable)
        {
            return EmptyPlan("No editor on this machine has written a log or a crash report.");
        }

        var (steps, measured) = await PlanDeletionsAsync(targets, keep, ct).ConfigureAwait(false);

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        // §5.3. The editor holds the current session's log folder open, so a running editor means
        // part of this will be skipped rather than removed. The names are the discovered folders'
        // for the reason VsCodeCacheProvider gives.
        if (RunningProcessNotice.For(Inspector, [.. editors.Select(e => e.Name)]) is { } warning)
        {
            notes.Add(warning);
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            ProtectedPaths = Protect(
                [.. survivors.Concat(declined).DistinctBy(s => s.Path, StringComparer.OrdinalIgnoreCase)]),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = unreadable,
            WasNotExamined = targets.Count == 0 && declined.Count > 0,
        };
    }

    /// <summary>
    /// Whether either declared name is on disk, by probing the table rather than by enumerating
    /// (G4). Two existence checks per editor, and neither can reach a path the table does not name.
    /// </summary>
    private static bool HasRecord(VsCodeUserData editor) =>
        FolderChildren.DisposableNames.Any(
            name => LongPath.DirectoryExists(Path.Combine(editor.Path, name)));
}
