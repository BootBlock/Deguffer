using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// A row that removes named directories from inside every Chromium user-data folder on the machine:
/// the folders of the Chromium-based browsers, and of the desktop applications that embed the same
/// engine directly or through WebView2.
///
/// <para><b>Two rows, because a row has one tier.</b> A plan carries its provider's tier, and that
/// tier decides pre-selection and the confirmation, so a child declared above the row's tier would be
/// planned and pre-selected under the row's. The engine's caches are Tier 1 and a service worker's
/// offline storage is Tier 2 (see <see cref="ChromiumServiceWorkerStorageProvider"/>), so each is a
/// subclass with its own table, and this class holds everything the two have in common: which folders
/// are Chromium's, which files in them must survive, when a folder is in use, and how a table is
/// walked. A third table is a third subclass, never a tier per child.</para>
///
/// <para><b>Each table is an exact allow-list, and that is the whole safety argument.</b> What sits
/// beside the named directories is Tier 3 and looks identical: <c>Local Storage</c>,
/// <c>Session Storage</c> and <c>IndexedDB</c> are directories in the same folder in the same naming
/// style, and <c>Local State</c>, <c>Cookies</c>, <c>Login Data</c> and <c>Web Data</c> are files among
/// them. Between them they hold sign-in tokens, saved passwords, saved payment cards, drafts and
/// offline application data. So this is §5.2 applied to a signature instead of to a root — a name the
/// table does not carry is Tier 4 by construction, and everything spared that is actually on disk is
/// asserted to survive.</para>
///
/// <para><b>A directory name is not on its own a licence to look inside a folder.</b> Any directory
/// anywhere may be called <c>GPUCache</c>, so identification is a separate and positive judgement:
/// <see cref="ChromiumUserDataDiscovery"/> requires the folder to hold the engine's own
/// <c>Local State</c> file, or the <c>LocalPrefs.json</c> a declared framework host writes instead,
/// before a row is ever asked what may go inside it. A table then says what may be deleted; it never
/// says whose folder this is.</para>
///
/// <para>§5.1 does not apply. No embedding application exposes a cache-eviction command, and the
/// engine's own clear-browsing-data surface is reachable only from inside the running process.
/// WebView2 documents one, <c>CoreWebView2Profile.ClearBrowsingDataAsync</c>, and only the host
/// application can call it.</para>
///
/// <para>§5.3 is a veto here as well as a warning. A folder a running program is using is left alone
/// whole, and the clean asks again before each removal. See <see cref="ChromiumFolderInUse"/>.</para>
///
/// <para>A packaged (MSIX) application's WebView2 folder is reached like any other, because it is
/// found by its own name wherever it sits. An Electron application packaged the same way is not:
/// Windows redirects its <c>%APPDATA%</c> to
/// <c>%LOCALAPPDATA%\Packages\&lt;family&gt;\LocalCache\Roaming</c>, below where a folder is
/// identified by <c>Local State</c> alone. See §3 of <c>docs/todo/unreached-locations.md</c>.</para>
/// </summary>
public abstract class ChromiumUserDataProvider : CleanupProviderBase
{
    /// <summary>
    /// Why the file that identified the user-data folder must survive. §5.6 asserts it by name
    /// because a <see cref="DisposableChildSet"/> only ever classifies a directory, so a file beside
    /// the caches is never enumerated, never classified, and never asserted unless it is named —
    /// the lesson NVIDIA's <c>accounts</c> taught, in a folder with a great deal more to lose. Which
    /// file that is belongs to the folder's <see cref="ChromiumLayout"/>.
    /// </summary>
    private const string IdentifyingFileReason =
        "The application's own settings, and the key that decrypts its saved cookies and passwords.";

    /// <summary>
    /// Why a framework host's partition keeps its own copy of the identifying file. It is what
    /// made the directory a profile, and it holds that partition's settings.
    /// </summary>
    private const string PartitionFileReason = "The settings of this part of the application's browser.";

    /// <summary>
    /// The same, per profile: the credential surface, named in full rather than sampled. Anything
    /// less makes the §5.6 evidence weaker than the claim it supports.
    ///
    /// <c>Cookies</c> is listed at both paths because Chromium moved it under <c>Network</c> and
    /// older profiles still keep it at the top level. Whichever is absent records itself as nothing
    /// to preserve rather than as a pass.
    /// </summary>
    private static readonly (string Name, string Reason)[] ProtectedProfileFiles =
    [
        ("Cookies", "Sign-in cookies. Removing them signs the user out of everything."),
        (@"Network\Cookies", "Sign-in cookies. Removing them signs the user out of everything."),
        ("Login Data", "Saved usernames and passwords."),
        ("Web Data", "Saved addresses and payment cards."),
    ];

    /// <summary>Why a folder a running program is using is asserted to survive whole.</summary>
    private const string InUseReason =
        "A running program is using this folder, so nothing inside it is removed.";

    private readonly ChromiumUserDataDiscovery _discovery;
    private readonly ILiveTreeInspector _liveTrees;
    private readonly RowDeclarations _declarations;
    private IReadOnlyList<ChromiumUserData>? _applications;
    private IReadOnlyList<ToolRoot>? _toolRoots;

    /// <param name="discovery">
    /// Shared between the rows that look inside the same folders, so one planning pass walks both
    /// application-data roots once. A row built without one finds the folders for itself.
    /// </param>
    /// <param name="declarations">
    /// What every row in the planner offers, so a child the other Chromium row or a VS Code row
    /// removes from the same folder is not asserted here. See <see cref="RowDeclarations"/>.
    /// </param>
    protected ChromiumUserDataProvider(
        IUserEnvironment? environment,
        IProcessRunner? runner,
        IProcessInspector? inspector,
        IDirectoryScanner? scanner,
        ILiveTreeInspector? liveTrees,
        ChromiumUserDataDiscovery? discovery,
        RowDeclarations? declarations)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
    {
        _discovery = discovery ?? new ChromiumUserDataDiscovery(Environment);
        _liveTrees = liveTrees ?? LiveTreeInspector.Default;
        _declarations = declarations ?? new RowDeclarations();
    }

    public sealed override StepGrain Grain => StepGrain.Parts;

    /// <summary>
    /// What this row removes from each profile, one <see cref="CacheLevel"/> per directory whose
    /// children it classifies. Every name it offers must be this row's <see cref="ICleanupProvider.Tier"/>.
    /// </summary>
    protected abstract IReadOnlyList<CacheLevel> CacheLevels { get; }

    /// <summary>What a plan says when no folder on the machine holds anything this row removes.</summary>
    protected abstract string NothingFound { get; }

    /// <summary>
    /// What this row removes, as a noun phrase that follows "beside", for the note that counts what
    /// was left alone around it.
    /// </summary>
    protected abstract string WhatIsRemoved { get; }

    /// <summary>
    /// The sentence a plan adds when it removed something from inside a directory that stays, so a
    /// user who sees that directory still standing afterwards can tell that what was inside it went.
    /// </summary>
    protected abstract string ContainerSentence { get; }

    /// <summary>
    /// The applications whose folders hold at least one directory this row removes, memoised for the
    /// life of a planning pass (G4).
    ///
    /// Exposed so tests can assert that no user-data folder is ever a target.
    /// </summary>
    public IReadOnlyList<ChromiumUserData> Applications(CancellationToken ct = default) =>
        _applications ??= [.. _discovery.Discover(ct).Where(app => HasRecognisedChild(app, ct))];

    /// <summary>
    /// §5.2 as §7.1 needs it read from outside: one root per level of every profile of every
    /// Chromium folder on the machine.
    ///
    /// <para>A root per level, on Cargo's reasoning — a declaration is an allow-list over one
    /// directory's immediate children, and these sit up to two deep. A root per <em>profile</em> as
    /// well, because a Chromium host repeats the whole layout inside <c>Default</c> and each
    /// <c>Profile N</c>, and a user-data folder that is not also declared per profile would refuse
    /// what this row removes.</para>
    ///
    /// <para>Built from every folder discovered rather than from <see cref="Applications"/>, which
    /// keeps only those already holding a recognised child. That filter is right for planning and
    /// wrong here: a folder with nothing to remove in it yet still holds <c>Login Data</c> and
    /// <c>Cookies</c>, and those are the reason this declaration exists.</para>
    ///
    /// <para>Both rows declare the same folders with disjoint tables. §7.1 reads the union of every
    /// declaration covering a path, so each row states only what it removes.</para>
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
        _toolRoots ??=
        [
            .. from application in _discovery.Discover()
               from profile in application.Profiles
               from level in CacheLevels
               select ToolRoot.Of(
                   level.Resolve(profile),
                   $"This is inside {application.Name}'s own folder. Deguffer removes what it "
                   + "recognises in there from the Storage page — the sign-in cookies, saved "
                   + "passwords and payment cards sit beside it.",
                   level.Children),
        ];

    public override void InvalidateCaches()
    {
        _applications = null;
        _toolRoots = null;
        _discovery.Invalidate();
        _liveTrees.Invalidate();
        base.InvalidateCaches();
    }

    /// <summary>
    /// Presence is something to remove actually on disk, never a folder existing. An application that
    /// embeds Chromium but has not run yet keeps a user-data folder with nothing in it to remove, and
    /// reporting that as a source would offer the user a row the plan then has nothing to say about.
    ///
    /// <para><b>A refused application-data root counts as present, and these are the rows where that
    /// matters.</b> Nearly every other provider decides presence by probing a path it already knows
    /// the name of, and a full path still resolves through a directory the account may not list.
    /// These decide by enumerating, so a refusal here answers "no source" and the row renders as
    /// "Not installed" — a stronger claim than "Already clear", and one made about a folder Deguffer
    /// never read. Answering true sends the pass into <see cref="BuildPlanAsync"/>, which says
    /// so.</para>
    ///
    /// <para>A browser Deguffer did not look inside, because a link or a refused segment stood in
    /// front of it, counts as present for the same reason: the plan is where that is said.</para>
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(
            Applications(ct).Count > 0
            || _discovery.UnreadableRoots.Count > 0
            || _discovery.Obstructed.Count > 0);

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var applications = Applications(ct);

        if (applications.Count == 0 && _discovery.Obstructed.Count == 0)
        {
            // A refused application-data root leaves this walk with nothing found and nothing said,
            // which is not the same as having looked and found none.
            return _discovery.UnreadableRoots.Count == 0
                ? EmptyPlan(NothingFound)
                : EmptyPlan(UnreadableRoot.WhyNothingWasPlanned(
                    string.Join("' and '", _discovery.UnreadableRoots))) with { HasUnreadableRoot = true };
        }

        var notes = new List<PlanNote>();
        var targets = new List<DeletionTarget>();
        var declined = new List<(string Path, string Reason)>();
        var survivors = new List<(string Path, string Reason)>();

        // Seeded rather than started at false. A root that refused to be listed is a fact about this
        // pass whether or not the *other* root turned up applications, and reading it only in the
        // "found nothing" arm below left it dropped in exactly the case where a plan gets rendered.
        var unreadable = _discovery.UnreadableRoots.Count > 0;

        foreach (var root in _discovery.UnreadableRoots)
        {
            notes.Add(UnreadableRoot.Note(root));
        }

        foreach (var obstacle in _discovery.Obstructed)
        {
            if (obstacle.IsLink)
            {
                notes.Add(CacheLevelWalk.Note(obstacle.Path));
                declined.Add((obstacle.Path, CacheLevelWalk.LinkReason));
            }
            else
            {
                notes.Add(UnreadableRoot.UnreachedNote(obstacle.Path));
                survivors.Add((obstacle.Path, UnreadableRoot.UnreachedReason));
                unreadable = true;
            }
        }

        var live = ChromiumFolderInUse.Find(_liveTrees, [.. applications.Select(a => a.Path)], ct);

        foreach (var application in applications)
        {
            ct.ThrowIfCancellationRequested();

            if (live.IsLive(application.Path))
            {
                survivors.Add((application.Path, InUseReason));
                continue;
            }

            var stillUnused = new ChromiumFolderInUse(_liveTrees, application.Path);

            if (application.ProfilesIncomplete)
            {
                notes.Add(UnreadableRoot.Note(application.Path));
                unreadable = true;
            }

            survivors.Add((
                application.Path,
                $"The '{application.Name}' data folder itself must survive — only recognised cache directories inside it are removed."));
            survivors.Add((Path.Combine(application.Path, application.Layout.IdentifyingFile), IdentifyingFileReason));

            var spared = 0;
            var emptiedAContainer = false;

            foreach (var profile in application.Profiles)
            {
                survivors.Add((
                    profile,
                    "The profile directory itself must survive — only recognised cache directories inside it are removed."));
                survivors.AddRange(ProtectedProfileFiles.Select(f => (Path.Combine(profile, f.Name), f.Reason)));

                if (application.Layout.Profiles is ChromiumProfileRule.Marked
                    && !profile.Equals(application.Path, StringComparison.OrdinalIgnoreCase))
                {
                    survivors.Add((Path.Combine(profile, application.Layout.IdentifyingFile), PartitionFileReason));
                }

                var walk = CacheLevelWalk.Under(CacheLevels, profile, ct);
                var spares = walk.Survivors(_declarations, ct);

                // Listed under the application and the profile, because those are what a reader
                // knows: nobody chooses between 'Code Cache' folders by name.
                var heading = profile.Equals(application.Path, StringComparison.OrdinalIgnoreCase)
                    ? application.Name
                    : $"{application.Name} — {Path.GetFileName(profile)}";

                targets.AddRange(walk.Targets.Select(target => target with { Group = heading, UseCheck = stillUnused }));
                declined.AddRange(walk.Declined);
                survivors.AddRange(spares);
                notes.AddRange(walk.Notes);

                spared += spares.Count;
                emptiedAContainer |= walk.EmptiedAContainer;
                unreadable |= walk.Unreadable;
            }

            // One note per application rather than one per spared child. A Chromium profile holds
            // dozens of directories, so naming each of them across ten applications would produce a
            // plan nobody reads — and a note nobody reads protects nothing. Each of them is still
            // asserted individually by §5.6, and this is the sentence that says so.
            //
            // The container sentence is not decoration. What a row removes from inside a directory
            // that is itself kept leaves that directory standing, and a user who sees it afterwards
            // has no way to tell that anything inside it went. It is said only when it happened.
            if (spared > 0)
            {
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"In '{application.Name}', {(application.ProfilesIncomplete ? "at least " : string.Empty)}"
                    + $"{spared} other {(spared == 1 ? "item is" : "items are")} left alone "
                    + $"beside {WhatIsRemoved}. Sign-in state, saved passwords and the application's other "
                    + "data all live in that folder, so only the directories Deguffer recognises are removed."
                    + (emptiedAContainer ? " " + ContainerSentence : string.Empty)));
            }
        }

        // Reaching here means HasRecognisedChild already found a directory on disk by full name, and
        // a full path resolves through a directory the account may not list. So the sentence below
        // would deny what this same row established one method earlier. A folder held back as in use
        // has its caches too.
        if (targets.Count == 0 && declined.Count == 0 && !unreadable && live.Live.Count == 0)
        {
            return EmptyPlan(NothingFound);
        }

        if (LiveTreeVeto.NoteFor(live.Live, held => $"'{applications.First(a => a.Path.Equals(held.Directory, StringComparison.OrdinalIgnoreCase)).Name}'")
            is { } inUse)
        {
            notes.Add(inUse);
        }

        if (LiveTreeVeto.IncompleteNote(live.Complete, "Close the applications before you clean.") is { } incomplete)
        {
            notes.Add(incomplete);
        }

        var (steps, measured) = await PlanDeletionsAsync(targets, keep, ct).ConfigureAwait(false);

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        // §5.3. It decides nothing: a miss costs one absent warning, and a hit names a process the
        // user can actually see.
        if (RunningProcessNotice.For(Inspector, [.. applications.Select(a => a.ProcessName)]) is { } warning)
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
            // The user-data folder is its own profile in the single-profile layout, so that one
            // directory is named twice on that path. Verifying it twice would report one survivor
            // as two.
            ProtectedPaths = Protect(
                [.. survivors.Concat(declined)]),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = unreadable,
            // A folder held back as in use and a cache behind a link are both something real left
            // unexamined, so a row with no steps must not read as clear.
            WasNotExamined = targets.Count == 0 && (declined.Count > 0 || live.Live.Count > 0),
        };
    }

    /// <summary>
    /// Whether any name this row offers is on disk for this application, by probing the table rather
    /// than by enumerating (G4). One existence check per name per profile, and not one of them can
    /// reach a path the table does not name.
    ///
    /// <para><b>A presence probe, not a safety gate.</b> It answers through a junction, so an
    /// application whose only cache is a link reports as present here and then yields no target,
    /// because <see cref="CacheLevelWalk"/> declines it. That is the intended outcome — a plan naming
    /// the link beats an empty plan that claims no cache exists — but a future edit must not read
    /// a true from this as licence to delete anything.</para>
    /// </summary>
    private bool HasRecognisedChild(ChromiumUserData application, CancellationToken ct)
    {
        foreach (var profile in application.Profiles)
        {
            foreach (var level in CacheLevels)
            {
                ct.ThrowIfCancellationRequested();

                var directory = level.Resolve(profile);

                if (level.Children.DisposableNames.Any(
                        name => LongPath.DirectoryMayExist(Path.Combine(directory, name))))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
