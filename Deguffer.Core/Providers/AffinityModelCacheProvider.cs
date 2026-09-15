using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The machine-learning models Affinity downloads for its selection and segmentation features
/// (~730 MB across two major versions on the measured machine).
///
/// <para>Affinity does not ship these models in its installer. The application fetches them the
/// first time a feature needs one and keeps them per major version, in <c>modelcache</c> inside its
/// shared folder. A machine that has run more than one version holds a copy per version, and an
/// uninstalled version leaves its copy behind: on the measured machine the whole Affinity 2 tree had
/// not been written to for eleven months, and 404 MB of it was models nothing would ever load again.
/// </para>
///
/// <para><b>Tier 2</b>, on <see cref="PlaywrightBrowsersProvider"/>'s reasoning. Nothing fetches a
/// model back in the background: the next subject or object selection is what discovers the model is
/// gone, and getting it back needs a connection and, on Affinity 3, a signed-in account. So the row
/// is offered and never pre-selected, and §7's acknowledgement applies.</para>
///
/// <para><b>§5.1 has an answer here, and it is not a command.</b> Affinity's own control is
/// Settings → Machine Learning, which lists each model category and installs or uninstalls it, and
/// the vendor's help says the models can be uninstalled to reclaim space and reinstalled when the
/// features are next wanted. It is a window rather than an executable, so Deguffer cannot call it —
/// but it is the route to prefer, and <see cref="ProviderDescription.Recommendation"/> names it.
/// </para>
///
/// <para><b>§5.2 is the reason this provider exists at all, beyond the megabytes.</b>
/// <c>modelcache</c> sits directly beside the user's entire asset library: <c>user</c> held 903 MB of
/// <c>.propcol</c> files on the measured machine, and <c>Licences</c> and <c>Receipts</c> are what
/// keep the product activated. A provider that targeted <c>Common\&lt;version&gt;</c> rather than the
/// <c>modelcache</c> inside it would take every one of them. So the version folder is a declared root
/// whose one recognised child is the model cache, everything else in it is Tier 4 by construction,
/// and every entry actually standing beside the cache is asserted to survive.</para>
///
/// <para><b>What Deguffer knows about this folder is direct observation, not documentation.</b> Serif
/// publishes nothing about clearing <c>modelcache</c>. That is said in the row rather than left out,
/// because a user deciding whether to let a cleaner near an application's folder is entitled to know
/// how well the claim is evidenced.</para>
/// </summary>
public sealed class AffinityModelCacheProvider : CleanupProviderBase
{
    /// <summary>The downloaded models, and the one child of a version folder that may ever go.</summary>
    public const string ModelCacheName = "modelcache";

    /// <summary>
    /// What a child of <c>Common\&lt;version&gt;</c> is. One recognised name, and the traps beside it
    /// declared rather than left to the unrecognised-child sentence — that sentence is true of them
    /// and says nothing, and these are the entries a reader most needs told apart from a cache.
    ///
    /// <para>Files are in here beside directories, which is a departure from
    /// <see cref="ChromiumCacheProvider"/>'s two tables. It is possible because this provider
    /// enumerates every entry standing beside the cache in order to assert it survived, so a file is
    /// classified by the route a directory is rather than needing a second one.</para>
    ///
    /// <para>A name absent from this list is Tier 4 all the same. The list exists to give a better
    /// sentence, never to decide the answer.</para>
    /// </summary>
    public static readonly IReadOnlyList<ChildClassification> SharedChildren =
    [
        new ChildClassification(
            ModelCacheName,
            SafetyTier.RegenerableWithCost,
            "Machine-learning models Affinity downloaded for its selection features. It downloads "
            + "them again the next time one of those features is used."),
        new ChildClassification(
            "user",
            SafetyTier.DoNotTouch,
            "Your asset library, raster brushes and vector brushes. Nothing re-creates them."),
        new ChildClassification(
            "Licences",
            SafetyTier.DoNotTouch,
            "What keeps the product activated."),
        new ChildClassification(
            "Receipts",
            SafetyTier.DoNotTouch,
            "Your proof of purchase, which activation is checked against."),
        new ChildClassification(
            "Settings",
            SafetyTier.DoNotTouch,
            "Settings shared by every Affinity product."),
        new ChildClassification(
            "Plugins",
            SafetyTier.DoNotTouch,
            "Plugins shared by every Affinity product."),
        new ChildClassification(
            "migrate",
            SafetyTier.DoNotTouch,
            "What Affinity carried forward from the version before this one."),
        new ChildClassification(
            "locks",
            SafetyTier.DoNotTouch,
            "Live state saying which Affinity products are running right now."),
        new ChildClassification(
            "clipboard",
            SafetyTier.DoNotTouch,
            "What Affinity products pass between one another through the clipboard."),
        new ChildClassification(
            "ipc.dat",
            SafetyTier.DoNotTouch,
            "Live state Affinity products use to talk to each other."),
        new ChildClassification(
            "cs.dat",
            SafetyTier.DoNotTouch,
            "Affinity's own record of its content store."),
        new ChildClassification(
            "cs.json",
            SafetyTier.DoNotTouch,
            "Affinity's own record of its content store."),
        new ChildClassification(
            "sp.db",
            SafetyTier.DoNotTouch,
            "Affinity's own database of the content you have installed."),
        new ChildClassification(
            "licence.dat",
            SafetyTier.DoNotTouch,
            "What keeps the product activated."),
    ];

    private static readonly DisposableChildSet Shared = new(SharedChildren);

    private IReadOnlyList<AffinityCommonTree>? _trees;

    public AffinityModelCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
    {
    }

    public override string Id => "affinity-model-cache";

    public override string Name => "Affinity machine-learning models";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    /// <summary>
    /// Each major version's models are a separate decision. Somebody who still opens Affinity 2 and
    /// has moved everything else to Affinity 3 wants one row gone and the other kept.
    /// </summary>
    public override StepGrain Grain => StepGrain.Items;

    public override string WhatHappensOnNextUse =>
        "The next time you use subject selection, object selection or another machine-learning "
        + "feature, Affinity downloads the models it needs before the feature will run — a few "
        + "hundred megabytes over an internet connection, and on Affinity 3 through the account you "
        + "are signed in with. Your documents, brushes, assets, licences and settings are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Affinity Photo, Designer and Publisher, a suite of image editing and design "
            + "applications",
        Publisher = "Serif, now part of Canva",
        Purpose = "Affinity's subject and object selection tools run machine-learning models that "
            + "are not part of the installer. The application downloads them the first time one of "
            + "those tools is used and keeps a separate copy for each major version it has run, so a "
            + "version you have since uninstalled can still be holding several hundred megabytes of "
            + "them.",
        Recommendation = "Affinity has its own control for this, and it is the one to prefer: "
            + "Settings → Machine Learning lists each model and lets you uninstall and reinstall it "
            + "from inside the application. Removing the folder reclaims the same space, and the "
            + "models are downloaded again when a feature next needs them. Deguffer removes only the "
            + "'modelcache' folder — your asset library, brushes, licences and settings sit directly "
            + "beside it and are left alone. Serif publishes nothing about clearing this folder; what "
            + "Deguffer knows about it is direct observation of what Affinity writes.",
    };

    /// <summary>
    /// §5.3. Affinity's own product executables, which map a model while a selection tool is working.
    /// Serif names three of them plainly enough that some other publisher could use the same name,
    /// and the consequence of that is one sentence on the plan rather than a refusal — so the set is
    /// the complete one rather than the single name nobody else would take.
    /// </summary>
    protected override IReadOnlyList<string> ConflictingProcessNames =>
        ["Affinity", "Photo", "Designer", "Publisher"];

    public override void InvalidateCaches()
    {
        base.InvalidateCaches();
        _trees = null;
    }

    /// <summary>
    /// Affinity's shared trees, memoised for the life of a planning pass (G4). §5.2's declaration and
    /// planning ask the same question of the same disk.
    /// </summary>
    public IReadOnlyList<AffinityCommonTree> Trees(CancellationToken ct = default) =>
        _trees ??= AffinityProfiles.Discover(Environment, ct);

    /// <summary>
    /// §5.2 as §7.1 needs it read from outside, and the levels are the whole safety argument. The
    /// profile root and <c>Common</c> recognise <em>nothing</em>: a version folder is never removed
    /// whole, and the per-product folders beside <c>Common</c> hold unsaved-document recovery. Only
    /// the version folder recognises a child, and the only child it recognises is the model cache.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots
    {
        get
        {
            var roots = new List<ToolRoot>();

            foreach (var tree in Trees())
            {
                roots.Add(new ToolRoot(
                    tree.Root,
                    "This is Affinity's own folder in your profile. Deguffer removes only the "
                    + "downloaded machine-learning models from inside it, because your asset library, "
                    + "your licences and your unsaved-document recovery are in here too.",
                    _ => false));

                if (tree.Common is { } common)
                {
                    roots.Add(new ToolRoot(
                        common,
                        "This is the folder every Affinity product shares. Each version folder in it "
                        + "holds your asset library and your licences, so Deguffer never removes one "
                        + "— only the model cache inside it.",
                        _ => false));
                }

                foreach (var version in tree.Versions)
                {
                    roots.Add(ToolRoot.Of(
                        LongPath.Display(version.FullName),
                        $"This is what Affinity {version.Name} shares between its products. Your "
                        + "asset library, brushes, licences and settings are in here, so Deguffer "
                        + "removes only the downloaded models beside them.",
                        Shared));
                }
            }

            return roots;
        }
    }

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(AffinityProfiles.RootsFor(Environment).Any(LongPath.DirectoryExists));

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var trees = Trees(ct);

        if (trees.Count == 0)
        {
            return EmptyPlan("Affinity has not been run on this machine.");
        }

        var found = new PlanUnderConstruction();

        foreach (var tree in trees)
        {
            ct.ThrowIfCancellationRequested();
            Examine(tree, found, ct);
        }

        var (steps, measured) = await PlanDeletionsAsync(found.Targets, keep, ct).ConfigureAwait(false);

        if (found.Targets.Count == 0 && found.Declined == 0)
        {
            found.Notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                "Affinity has not downloaded any machine-learning models on this machine."));
        }

        if (measured.Note is { } scanNote)
        {
            found.Notes.Add(scanNote);
        }

        if (BuildRunningProcessNote() is { } warning)
        {
            found.Notes.Add(warning);
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            ProtectedPaths = Protect([.. found.Protect]),
            Notes = found.Notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = found.Unreadable,
            WasNotExamined = found.Targets.Count == 0 && found.Declined > 0,
        };
    }

    /// <summary>One profile root: what may go inside it, and what the plan must say about the rest.</summary>
    private static void Examine(AffinityCommonTree tree, PlanUnderConstruction found, CancellationToken ct)
    {
        found.Protect.Add((
            tree.Root,
            "Affinity's own folder must survive — only downloaded models inside it are removed."));

        if (tree.LinkedAway is { } linked)
        {
            found.Decline(new PlanNote(
                PlanNoteSeverity.Information,
                $"Leaving '{linked}' alone: it is a link to somewhere else, and Deguffer does not "
                + "look through a link."));
            return;
        }

        if (tree.Common is not { } common)
        {
            return;
        }

        found.Protect.Add((
            common,
            "The folder Affinity's products share must survive; only a model cache inside it goes."));

        // The folder was found on disk by name, and a listing right is separate from a traverse right
        // — so a refusal here leaves a plan with no steps and, without this, nothing said. The shell
        // renders that as "Already clear", which is a claim about a folder nobody read.
        if (tree.Unreadable)
        {
            found.Unread(common);
        }

        // A link is a child the user can see, so it is named rather than dropped. It is never
        // followed: what it points at was never classified.
        foreach (var link in tree.Links)
        {
            found.Decline(
                new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Leaving '{link.Name}' alone: it is a link to somewhere else, and Deguffer does "
                    + "not delete through a link."),
                (LongPath.Display(link.FullName),
                    "A link rather than a directory, so what it points at was never classified."));
        }

        foreach (var stranger in tree.Unrecognised)
        {
            // §5.2: unrecognised means untouched, and the user is told rather than left to wonder why
            // the total is smaller than the folder.
            const string Why = "not a folder Affinity names for one of its versions.";

            found.Decline(
                new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Leaving '{stranger.Name}' alone: {Why}"),
                (LongPath.Display(stranger.FullName), Why));
        }

        foreach (var version in tree.Versions)
        {
            ct.ThrowIfCancellationRequested();
            ExamineVersion(tree, LongPath.Display(version.FullName), version.Name, found, ct);
        }
    }

    /// <summary>
    /// One shared version folder: the model cache in it, if there is one, and every entry standing
    /// beside that cache.
    ///
    /// <para><b>The whole folder is enumerated, and a folder that will not be enumerated is left
    /// alone.</b> Probing for <c>modelcache</c> by name would answer through a folder the account may
    /// not list, because traversing and listing are separate rights — and that is precisely the case
    /// where §5.6's negative cannot be asserted, since the asset library beside the cache was never
    /// seen. A cache Deguffer takes without being able to name what it left standing is the one
    /// reclaim not worth having.</para>
    /// </summary>
    private static void ExamineVersion(
        AffinityCommonTree tree,
        string version,
        string name,
        PlanUnderConstruction found,
        CancellationToken ct)
    {
        found.Protect.Add((
            version,
            $"Affinity {name}'s shared folder must survive — it holds the asset library, the brushes "
            + "and the licences."));

        // FolderEntries rather than an enumeration written here, because it keeps the two answers
        // apart that a hand-written catch merges: a folder that is gone holds nothing, and a folder
        // that refused to be listed is not empty. Reporting the first as the second puts a sentence
        // about permissions against a folder nobody has.
        if (FolderEntries.Of(version) is not { } entries)
        {
            found.Unread(version);
            return;
        }

        DirectoryInfo? cache = null;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var path = LongPath.Display(entry.FullName);

            if (!string.Equals(entry.Name, ModelCacheName, StringComparison.OrdinalIgnoreCase))
            {
                // Everything else in the folder, asserted by name. The spared and the targeted are
                // siblings, which is exactly where an over-broad rule takes one with the other — and
                // what is beside this cache is an asset library and an activation record, not cache.
                found.Protect.Add((path, Shared.Classify(entry.Name).Reason));
                continue;
            }

            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                found.Decline(
                    new PlanNote(
                        PlanNoteSeverity.Information,
                        $"Leaving Affinity {name}'s '{ModelCacheName}' alone: it is a link to "
                        + "somewhere else, and Deguffer does not delete through a link."),
                    (path, "A link rather than a directory, so what it points at was never classified."));
                continue;
            }

            if (entry is DirectoryInfo directory)
            {
                cache = directory;
                continue;
            }

            // Affinity writes a folder here. A file of that name is something else, and the declared
            // reason for the name would describe it to the user as a download Affinity fetches again
            // — which is a sentence about the cache, against a path being left alone.
            found.Protect.Add((path, "A file where Affinity keeps a folder, so Deguffer left it alone."));
        }

        if (cache is null)
        {
            return;
        }

        found.Targets.Add(new DeletionTarget(
            LongPath.Display(cache.FullName),
            $"Machine-learning models Affinity {name} downloaded, fetched again the next time a "
            + "selection feature needs them.",

            // §7's age. A model file is written when it is downloaded and using it does not rewrite
            // it, so this is when Affinity last fetched a model — which is what separates the version
            // in daily use from the one an uninstall left behind.
            DirectoryAge.Of(cache.FullName, ct),

            // The version is the item, whichever root this machine keeps it under, so a cache kept
            // for a version somebody still opens stays kept. The root's folder name is in the key
            // because both roots can hold the same version number.
            Identity: new ItemIdentity(
                $@"{Path.GetFileName(tree.Root)}\{name}",
                $"Affinity {name} machine-learning models"),
            Facets: [new ItemFacet("Version", name)]));
    }

    /// <summary>
    /// What the walk of both roots has accumulated so far. It exists because the walk is three levels
    /// deep and every level can add a note, a protected path and a reason not to call the result
    /// "already clear" — passing five collections and two counters down by reference is where one
    /// level quietly stops contributing to one of them.
    /// </summary>
    private sealed class PlanUnderConstruction
    {
        public List<PlanNote> Notes { get; } = [];

        public List<DeletionTarget> Targets { get; } = [];

        public List<(string Path, string Reason)> Protect { get; } = [];

        /// <summary>Whether any folder refused to be listed, for <see cref="CleanupPlan.HasUnreadableRoot"/>.</summary>
        public bool Unreadable { get; set; }

        /// <summary>
        /// How many things Deguffer looked at and chose to leave. A plan with no steps and none of
        /// these examined an empty machine; one with no steps and some of these did not examine what
        /// it found, and §7 renders only the first as "already clear".
        /// </summary>
        public int Declined { get; set; }

        public void Decline(PlanNote note, (string Path, string Reason)? protect = null)
        {
            Notes.Add(note);
            Declined++;

            if (protect is { } path)
            {
                Protect.Add(path);
            }
        }

        /// <summary>
        /// A folder Windows would not list. It counts among <see cref="Declined"/> for the reason a
        /// link does: the plan under it is empty because nothing was read, not because there was
        /// nothing there, and the sentence saying Affinity has downloaded no models must not be said
        /// about it. Written once so the two levels that can meet a refusal cannot answer differently.
        /// </summary>
        public void Unread(string folder)
        {
            Notes.Add(UnreadableRoot.Note(folder));
            Unreadable = true;
            Declined++;
        }
    }
}
