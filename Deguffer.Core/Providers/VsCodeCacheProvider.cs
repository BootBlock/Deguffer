using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The caches a Code - OSS editor manages itself, which are the ones no Chromium rule reaches
/// (2.0 GB on the measured machine, against about 15 MB for the six Chromium names in the same
/// folder).
///
/// <para><b>Why this is not the Chromium provider.</b> A VS Code user-data folder holds
/// <c>Local State</c>, so <see cref="ChromiumCacheProvider"/> already reaches the six engine caches
/// inside it. Those six are the small part. <c>CachedData</c>, <c>CachedExtensionVSIXs</c>,
/// <c>CachedExtensions</c>, <c>CachedProfilesData</c> and <c>WebStorage</c> are the editor's own,
/// under names that belong to Code - OSS rather than to Chromium, and a rule about Chromium has no
/// business knowing them. So they are a second declaration over the same folder, and the two
/// providers target disjoint children of it.</para>
///
/// <para><b>Recognised by shape, not by the folder's name.</b> Every Code - OSS derivative writes
/// these same directory names into its own <c>%APPDATA%</c> folder, so
/// <see cref="VsCodeUserDataDiscovery"/> identifies the folder positively and this provider then
/// says what inside it may go. A hard-coded <c>Code</c> would reach one editor and miss Insiders,
/// VSCodium and every other derivative on the machine.</para>
///
/// <para><b>What sits beside these is the most valuable thing in the profile.</b> <c>User</c> holds
/// <c>workspaceStorage</c>, <c>globalStorage</c> and <c>History</c> — 14 GB between them on the
/// measured machine, and every byte of it user data wearing a cache costume (§4.3). It is Tier 4 by
/// construction like any unrecognised child, and <see cref="VsCodeUserDataDiscovery.NeverOffered"/>
/// names it in full so that a run produces evidence it survived rather than merely never mentioning
/// it.</para>
///
/// <para><b><c>CachedData</c> goes whole, and the issue that proposed this asked for one folder to
/// be spared.</b> It holds one directory per editor build, named by that build's commit, and a
/// folder whose commit is not the installed build can never be used again. Sparing the installed
/// one would need to know which it is, and nothing in the user-data folder records it — the
/// editor's own cleaner reads the commit from <c>product.json</c> in its install directory, which is
/// not discoverable from here for an arbitrary derivative. Guessing is the one thing §5.2 refuses,
/// and the guess would buy very little: every folder under <c>CachedData</c> is the V8
/// compiled-code cache, which is Tier 1 whichever build wrote it. Including the installed build's
/// costs exactly what Tier 1 promises, one slower start, and the same artefact under Chromium's own
/// name (<c>Code Cache</c>) is already offered whole for that reason.</para>
///
/// <para>§5.1 does not apply. The editor has no cache-eviction command: its own
/// <c>CachedDataCleaner</c> runs unattended after startup and is not reachable from a command line,
/// and the CLI's own help (checked against 1.136.0) exposes no option that clears any of
/// these.</para>
/// </summary>
public sealed class VsCodeCacheProvider : CleanupProviderBase
{
    /// <summary>
    /// What may be deleted from the user-data folder itself. Anything not named here is Tier 4 by
    /// construction, which is what makes "we did not recognise that" fail closed beside a folder
    /// holding the editor's entire stored state.
    ///
    /// <para><see cref="VsCodeWebStorage.DirectoryName"/> appears as a Tier 4 entry rather than as
    /// an omission, on the reasoning <see cref="ChromiumCacheProvider"/> settled for <c>Cache</c> and
    /// <c>Service Worker</c>: it is the one case where the unrecognised-child wording would be
    /// actively misleading, because the directory really is left standing and something inside it
    /// really is being removed.</para>
    /// </summary>
    public static readonly DisposableChildSet FolderChildren = new(
    [
        new ChildClassification(
            "CachedData",
            SafetyTier.RegenerableCache,
            "Compiled JavaScript and WebAssembly, one folder per editor build. The editor recompiles "
            + "its own code the first time it starts again, and the folders belonging to builds you "
            + "no longer have can never be used at all."),
        new ChildClassification(
            "CachedExtensionVSIXs",
            SafetyTier.RegenerableCache,
            "The extension packages the editor downloaded, kept after it installed them. The "
            + "extensions themselves are installed elsewhere and are not affected; this is only the "
            + "downloaded copy, which the marketplace supplies again if it is ever needed."),
        new ChildClassification(
            "CachedExtensions",
            SafetyTier.RegenerableCache,
            "The editor's own record of what it found when it last scanned the installed extensions. "
            + "It rebuilds the scan at startup."),
        new ChildClassification(
            "CachedProfilesData",
            SafetyTier.RegenerableCache,
            "The same extension scan, cached once per editor profile. It is rebuilt the next time "
            + "each profile is used."),
        new ChildClassification(
            VsCodeWebStorage.DirectoryName,
            SafetyTier.DoNotTouch,
            "The storage of every webview the editor has opened. Only the 'CacheStorage' inside each "
            + "numbered partition is removed, and the partition itself stays — what a webview saved "
            + "sits beside it."),
    ]);

    /// <summary>
    /// What may be deleted from one webview storage partition. Exactly one name, and the reason the
    /// rest is Tier 4 is that a partition holds the whole Chromium storage set: <c>Local Storage</c>,
    /// <c>IndexedDB</c> and <c>Session Storage</c> can each appear in there holding what a webview
    /// saved, in the same folder in the same naming style.
    /// </summary>
    public static readonly DisposableChildSet PartitionChildren = new(
    [
        new ChildClassification(
            "CacheStorage",
            SafetyTier.RegenerableCache,
            "Responses a webview stored so it would not fetch the same thing twice. They are fetched "
            + "again the next time that view is opened."),
    ]);

    /// <summary>
    /// Nothing. <see cref="VsCodeWebStorage.DirectoryName"/> is entered rather than emptied, so §7.1 must
    /// refuse every one of its children — the numbered partitions included, because a partition
    /// holds what a webview saved as well as what it cached.
    /// </summary>
    private static readonly DisposableChildSet WebStorageChildren = new([]);

    private readonly VsCodeUserDataDiscovery _discovery;
    private IReadOnlyList<Editor>? _installed;
    private IReadOnlyList<ToolRoot>? _toolRoots;

    public VsCodeCacheProvider(
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

    public override string Id => "vscode-cache";

    public override string Name => "VS Code editor caches";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override string WhatHappensOnNextUse =>
        "The editor recompiles its own code and rescans its extensions the first time it starts " +
        "again, so it opens more slowly once. Your settings, extensions, workspace state and local " +
        "history are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Visual Studio Code, and the editors built on the same Code - OSS base — "
            + "VS Code Insiders, VSCodium, Cursor and the rest",
        Publisher = "Microsoft publishes Visual Studio Code; each derivative is published by its "
            + "own vendor",
        Purpose = "The editor keeps its compiled code once per build it has ever run, the "
            + "installer package of every extension it downloaded, the result of its last extension "
            + "scan, and one web cache per webview it has opened. Nothing prunes most of it, so it "
            + "accumulates one folder per update for as long as the editor is installed.",
        Recommendation = "Every one of these is rebuilt or re-downloaded on demand, and the folders "
            + "belonging to editor builds you no longer have can never be used again at all. "
            + "Deguffer leaves the 'User' folder alone, which is where your settings, your workspace "
            + "state and your local file history live.",
    };

    /// <summary>
    /// The editors whose folders hold at least one recognised cache, memoised for the life of a
    /// planning pass (G4). Presence and planning ask the same question of the same disk.
    ///
    /// Exposed so tests can assert that no user-data folder is ever a target.
    /// </summary>
    public IReadOnlyList<VsCodeUserData> Editors(CancellationToken ct = default) =>
        [.. Installed(ct).Where(HasRecognisedCache).Select(e => e.UserData)];

    /// <summary>
    /// §5.2 as §7.1 needs it read from outside: the user-data folder, the <c>WebStorage</c>
    /// directory, and each numbered partition inside it.
    ///
    /// <para>A root per level, on Cargo's reasoning — a declaration is an allow-list over one
    /// directory's immediate children, and the partition caches sit three deep. <c>WebStorage</c>
    /// recognises nothing at all, which is deliberate: it is entered rather than emptied, so Explore
    /// must refuse every partition in it while still allowing the one cache named inside a
    /// partition.</para>
    ///
    /// <para>Built from every folder discovered rather than from <see cref="Editors"/>, which keeps
    /// only those already holding a recognised cache. That filter is right for planning and wrong
    /// here: a folder with no cache in it yet still holds the whole <c>User</c> tree, and that is
    /// the reason this declaration exists.</para>
    ///
    /// <para><b>This folder is declared twice, by two providers, and that is correct.</b>
    /// <see cref="ChromiumCacheProvider"/> declares the same path with the six engine cache names,
    /// and this one declares it with the editor's own. §7.1 reads the union of every declaration
    /// covering a path, so each provider states only what it knows and neither has to carry the
    /// other's table.</para>
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
        _toolRoots ??=
        [
            .. from editor in Installed()
               from root in RootsOf(editor)
               select root,
        ];

    public override void InvalidateCaches()
    {
        _installed = null;
        _toolRoots = null;
        base.InvalidateCaches();
    }

    /// <summary>
    /// Presence is a cache actually on disk, never a folder existing. An editor that has been
    /// installed and not yet run keeps a user-data folder with nothing disposable in it, and
    /// reporting that as a source would offer the user a row the plan then has nothing to say about.
    ///
    /// <para>A refused application-data root counts as present, on
    /// <see cref="ChromiumCacheProvider"/>'s reasoning: this provider decides presence by
    /// enumerating, so a refusal there would answer "no source" and render the row as "Not
    /// installed" — a claim about a folder Deguffer never read.</para>
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(Editors(ct).Count > 0 || _discovery.UnreadableRoots.Count > 0);

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var editors = Installed(ct).Where(HasRecognisedCache).ToList();

        if (editors.Count == 0)
        {
            // A refused application-data root leaves this walk with nothing found and nothing said,
            // which is not the same as having looked and found none.
            return _discovery.UnreadableRoots.Count == 0
                ? EmptyPlan("No editor on this machine keeps a cache in its own folder.")
                : EmptyPlan(UnreadableRoot.WhyNothingWasPlanned(_discovery.UnreadableRoots[0]))
                    with { HasUnreadableRoot = true };
        }

        var notes = new List<PlanNote>();
        var targets = new List<DeletionTarget>();
        var declined = new List<(string Path, string Reason)>();
        var survivors = new List<(string Path, string Reason)>();

        // Seeded rather than started at false. A root that refused to be listed is a fact about this
        // pass whether or not editors were found elsewhere under it.
        var unreadable = _discovery.UnreadableRoots.Count > 0;

        foreach (var root in _discovery.UnreadableRoots)
        {
            notes.Add(UnreadableRoot.Note(root));
        }

        foreach (var editor in editors)
        {
            ct.ThrowIfCancellationRequested();

            var folder = editor.UserData.Path;

            survivors.Add((
                folder,
                $"The '{editor.UserData.Name}' folder itself must survive — only recognised caches "
                + "inside it are removed."));
            survivors.AddRange(VsCodeUserDataDiscovery.NeverOffered
                .Select(n => (Path.Combine(folder, n.RelativePath), n.Reason)));

            var spared = editor.Partitions.Spared(folder, survivors, declined, notes);

            if (editor.Partitions.Unreadable)
            {
                notes.Add(UnreadableRoot.Note(Path.Combine(folder, VsCodeWebStorage.DirectoryName)));
                unreadable = true;
            }

            var outcome = CacheLevelWalk.Collect(folder, LevelsOf(editor), targets, declined, survivors, notes, ct);

            spared += outcome.Spared;
            unreadable |= outcome.Unreadable;

            // One note per editor rather than one per spared child. A user-data folder holds dozens
            // of directories, and a note nobody reads protects nothing. Each of them is still
            // asserted individually by §5.6, and this is the sentence that says so.
            //
            // The second sentence is not decoration. The partition caches sit inside directories
            // that are themselves kept, so a user who sees 'WebStorage' still standing after a clean
            // has no way to tell that anything inside it went. It is said only when it happened.
            if (spared > 0)
            {
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"In '{editor.UserData.Name}', {spared} other {(spared == 1 ? "item is" : "items are")} "
                    + "left alone beside the caches. Your settings, your workspace state and your local file "
                    + "history all live in that folder, so only the recognised caches are removed."
                    + (outcome.EmptiedAContainer
                        ? $" The webview caches sit inside '{VsCodeWebStorage.DirectoryName}', and that directory "
                          + "stays: only the one recognised cache inside each partition is removed."
                        : string.Empty)));
            }
        }

        if (targets.Count == 0 && declined.Count == 0 && !unreadable)
        {
            return EmptyPlan("No editor on this machine keeps a cache in its own folder.");
        }

        var (steps, measured) = await PlanDeletionsAsync(targets, keep, ct).ConfigureAwait(false);

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        // §5.3. The process names are not declared, because the editors are discovered rather than
        // known — so the folder's name stands in for the process's, which is what a Code - OSS
        // derivative does in practice: 'Code' writes Code.exe, 'Cursor' writes Cursor.exe. It
        // decides nothing: a miss costs one absent warning, and a hit names a process the user can
        // see and close.
        if (RunningProcessNotice.For(Inspector, [.. editors.Select(e => e.UserData.Name)]) is { } warning)
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
    /// Every discovered editor with its partitions read, memoised for the life of a planning pass
    /// (G4). The partitions are needed by presence, by planning and by <see cref="ToolRoots"/>, and
    /// re-reading <c>WebStorage</c> three times per editor would be three walks for one answer.
    /// </summary>
    private IReadOnlyList<Editor> Installed(CancellationToken ct = default) =>
        _installed ??= [.. _discovery.Discover(ct).Select(u => new Editor(u, VsCodeWebStorage.Of(u.Path)))];

    /// <summary>
    /// The levels this editor's caches sit in: the folder itself, then one per webview partition.
    ///
    /// A level per partition rather than a wider child set, on the reasoning <see cref="CacheLevel"/>
    /// records: §5.2's declaration is an allow-list over one directory's immediate children, so a
    /// cache three levels down cannot be one of its entries without turning "which children may this
    /// tool delete?" into "which paths, at what depth, may it reach?".
    /// </summary>
    private static IReadOnlyList<CacheLevel> LevelsOf(Editor editor) =>
    [
        new CacheLevel(string.Empty, FolderChildren),
        .. editor.Partitions.Numbered.Select(
            name => new CacheLevel(Path.Combine(VsCodeWebStorage.DirectoryName, name), PartitionChildren)),
    ];

    private static IEnumerable<ToolRoot> RootsOf(Editor editor)
    {
        yield return ToolRoot.Of(
            editor.UserData.Path,
            $"This is {editor.UserData.Name}'s own folder. Deguffer removes the caches in there from "
            + "the Storage page, where it knows which of them are caches — your settings, your "
            + "workspace state and your local file history sit beside them.",
            FolderChildren);

        var webStorage = Path.Combine(editor.UserData.Path, VsCodeWebStorage.DirectoryName);

        yield return ToolRoot.Of(
            webStorage,
            "This holds the storage of every webview the editor has opened. Deguffer removes the web "
            + "cache inside each one and leaves the rest, because what a webview saved sits beside "
            + "what it cached.",
            WebStorageChildren);

        foreach (var partition in editor.Partitions.Numbered)
        {
            yield return ToolRoot.Of(
                Path.Combine(webStorage, partition),
                "This is one webview's storage. Deguffer removes the web cache inside it and nothing "
                + "else.",
                PartitionChildren);
        }
    }

    /// <summary>
    /// Whether any declared name is on disk for this editor, by probing the tables rather than by
    /// enumerating (G4). Five existence checks plus one per partition, and not one of them can reach
    /// a path the tables do not name.
    ///
    /// <para><b>A presence probe, not a safety gate.</b> It answers through a junction, so an editor
    /// whose only cache is a link reports as present here and then yields no target, because
    /// <see cref="CacheLevelWalk"/> declines it. That is the intended outcome — a plan naming the
    /// link beats an empty plan that claims no cache exists — but a future edit must not read a true
    /// from this as licence to delete anything.</para>
    ///
    /// <para><b>A <c>WebStorage</c> that refused to be listed counts as a cache.</b> The partitions
    /// are reached by enumerating it rather than by name, so a refusal there leaves this with
    /// nothing to find — and an editor whose webview caches are its only ones would then report as
    /// absent and render the row as "Not installed", which is a claim about a directory Deguffer was
    /// not allowed to read. Answering true sends the pass into <see cref="BuildPlanAsync"/>, which
    /// says so instead.</para>
    /// </summary>
    private static bool HasRecognisedCache(Editor editor)
    {
        if (editor.Partitions.Unreadable)
        {
            return true;
        }

        foreach (var level in LevelsOf(editor))
        {
            var directory = level.Resolve(editor.UserData.Path);

            if (level.Children.DisposableNames.Any(
                    name => LongPath.DirectoryExists(Path.Combine(directory, name))))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One discovered editor, with what <c>WebStorage</c> turned out to hold.</summary>
    private sealed record Editor(VsCodeUserData UserData, VsCodeWebStorage Partitions);
}
