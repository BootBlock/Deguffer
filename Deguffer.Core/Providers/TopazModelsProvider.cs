using Deguffer.Core.Execution;
using Deguffer.Core.InstalledApps;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The machine-learning models Topaz Labs' applications download, kept machine-wide in
/// <c>%PROGRAMDATA%\Topaz Labs LLC\&lt;Product&gt;\models</c>. Topaz states 51 GB for Topaz Photo and
/// 48 GB for Topaz Gigapixel, and newer models run to several gigabytes each.
///
/// <para><b>Not measured.</b> No machine with a Topaz product was available when this was written,
/// so every rule here comes from Topaz's documentation and from what its staff and users report.
/// <c>docs/cache-locations.md</c> lists the sources, and <see cref="TopazLayout"/> holds the rules.</para>
///
/// <para><b>Products that are no longer installed come first.</b> Topaz renamed its products (Photo
/// AI became Photo, and the data folder was renamed with it) and discontinued the classic ones such
/// as DeNoise AI and Sharpen AI, so a models folder whose product is gone is weight nothing will load
/// again. <see cref="TopazInstallations"/> decides that, and a refusal never decides it. Those items
/// are listed first, under their own heading.</para>
///
/// <para><b>Tier 2.</b> An installed product downloads a model again when a feature next needs it,
/// which needs a connection and a signed-in, licensed Topaz account, and Gigapixel does so only while
/// its in-app model downloads are on. Topaz's documented recovery for a missing model is the
/// installer's Repair.</para>
///
/// <para><b>§5.1.</b> Topaz Video's File → Model Manager downloads models, and Photo's and Gigapixel's
/// installers choose which models to install. None of them removes one, so there is no route to
/// prefer, and the row names them as the way back.</para>
///
/// <para><b>§5.3.</b> While a program runs from a Topaz program folder, or a Topaz application or an
/// image editor its plugins run inside is running, a product that may be installed keeps its models: a
/// model loaded during the removal would leave a part-removed set. See <see cref="TopazModelsUse"/>.
/// The clean asks again before each removal.</para>
///
/// <para><b>No administrator rights are declared, and that is unmeasured.</b> The applications
/// download models on demand into these folders as the signed-in user, so that user can write there.
/// A model an installer placed may carry rights that refuse an ordinary account. Then the removal is
/// refused and reported, which is the direction <see cref="EpicLauncherContentCacheProvider"/> explains
/// is the right one to be wrong in, and the row's cost names the installer's Repair as the way back
/// from a part-removed set.</para>
/// </summary>
public sealed class TopazModelsProvider : CleanupProviderBase
{
    private const string NotInstalledGroup = "No longer installed";

    private const string InstalledGroup = "Installed";

    private const string UnlistedReason =
        "Deguffer could not list this folder, so nothing in it was examined and it is left alone.";

    private readonly ISystemDirectories _system;
    private readonly IUninstallRegistry _registry;
    private readonly ILiveTreeInspector _liveTrees;
    private readonly PassMemo<TopazSurvey> _survey = new();

    public TopazModelsProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null,
        IUninstallRegistry? registry = null,
        ILiveTreeInspector? liveTrees = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner)
    {
        _system = system ?? SystemDirectories.Current;
        _registry = registry ?? WindowsUninstallRegistry.Default;
        _liveTrees = liveTrees ?? LiveTreeInspector.Default;
    }

    public override string Id => "topaz-models";

    public override string Name => "Topaz Labs AI models";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    /// <summary>Each product's models are a separate decision, and a product that is gone is an easy one.</summary>
    public override StepGrain Grain => StepGrain.Items;

    public override string WhatHappensOnNextUse =>
        "Nothing, for a Topaz product that is no longer installed. An installed one downloads each model "
        + "again the next time a feature needs it, which needs an internet connection and a signed-in, "
        + "licensed Topaz account. Gigapixel does this only while 'Enable in-app model downloads' is on in "
        + "its preferences. If a model will not load afterwards, run the product's installer again and "
        + "choose Repair. Your projects, presets and settings are untouched.";

    public override ProviderBasis Basis => ProviderBasis.Researched;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.MediaAndCreative,
        Application = "Topaz Photo, Gigapixel and Video, and the discontinued DeNoise AI, Sharpen AI and "
            + "Mask AI, AI image and video enhancement applications",
        Publisher = "Topaz Labs",
        Purpose = "Topaz's applications download the machine-learning models behind their filters rather "
            + "than installing them, and keep every model you have used for every user of the computer. "
            + "Topaz states around 50 GB each for Photo and Gigapixel. A product you have uninstalled, or "
            + "one Topaz renamed, leaves its models behind.",
        Recommendation = "Remove the models of products that are no longer installed first: nothing will "
            + "load them again. An installed product's models come back only by downloading them, and Topaz "
            + "Video's Model Manager or the installer's Repair is how to fetch them. Deguffer removes only "
            + "a product's 'models' folder, never its logs, its sign-in record or the application.",
    };

    protected override IReadOnlyList<string> ConflictingProcessNames => TopazLayout.ProcessNames;

    public override void InvalidateCaches()
    {
        base.InvalidateCaches();
        _liveTrees.Invalidate();
        _survey.Forget();
    }

    private string Root => TopazLayout.Root(_system);

    /// <summary>One look at the vendor folder for a planning pass (G4), shared by §5.2's declaration and the plan.</summary>
    private TopazSurvey Survey(CancellationToken ct = default) =>
        _survey.Get(() =>
        {
            var installations = TopazInstallations.Read(_system, _registry, Environment);

            return TopazLayout.Survey(Root, installations, ct) with { ProgramFolders = installations.ProgramFolders };
        });

    private TopazModelsUse Use(TopazSurvey survey) => new(Inspector, _liveTrees, survey.ProgramFolders);

    /// <summary>
    /// §5.2 as §7.1 reads it. The vendor folder recognises nothing, so no product folder is removed
    /// whole. A product folder recognises <c>models</c> only where the plan would take it or go through
    /// it, and an outer <c>models</c> holding the sign-in record recognises only the inner one.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots
    {
        get
        {
            List<ToolRoot> roots =
            [
                new ToolRoot(
                    Root,
                    "This is Topaz Labs' folder for every user of this computer. Deguffer removes only a "
                    + "product's downloaded models from inside it, never a product's own folder.",
                    static _ => false),
            ];

            foreach (var product in Survey().Products)
            {
                var models = Path.Combine(product.Folder, TopazLayout.ModelsName);
                var target = product.Target;

                roots.Add(ToolRoot.Folders(
                    product.Folder,
                    $"This is {product.Name}'s folder. Deguffer removes only the downloaded models inside it, "
                    + "because its logs and its own files are in here too.",
                    name => target is not null
                        && name.Equals(TopazLayout.ModelsName, StringComparison.OrdinalIgnoreCase)
                        && LongPath.Contains(models, target)));

                if (target is not null && !target.Equals(models, StringComparison.OrdinalIgnoreCase))
                {
                    roots.Add(ToolRoot.Folders(
                        models,
                        $"This is {product.Name}'s models folder, which holds its sign-in record. Deguffer "
                        + "removes only the model data in the folder inside it.",
                        name => name.Equals(TopazLayout.ModelsName, StringComparison.OrdinalIgnoreCase)));
                }
            }

            return roots;
        }
    }

    /// <summary>§7.1: while a Topaz program runs, Explore refuses what the plan holds back, with its reason.</summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default)
    {
        var survey = Survey(ct);
        var (held, reason) = Held(survey, ct);

        return Task.FromResult<IReadOnlyList<ToolRoot>>(
        [
            .. held.Select(product => new ToolRoot(product.Target!, reason, static _ => false)),
        ]);
    }

    /// <summary>
    /// A models folder that may hold something, a vendor folder Deguffer could not read, or a link
    /// where Topaz's folders are, whose plan says it was not followed. A folder holding no models is no
    /// row.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default)
    {
        var survey = Survey(ct);

        return Task.FromResult(
            survey.Reach is TopazRootReach.Refused or TopazRootReach.Unlisted or TopazRootReach.Link
            || survey.Links.Count > 0
            || survey.Products.Any(product =>
                product.Unreadable
                || product.Declined is not null
                || (product.Target is { } target && DirectoryContent.MayBePresent(target))));
    }

    /// <summary>Every <c>models</c> folder below the vendor folder, whichever product or level it is at.</summary>
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>([CleanedPlace.FoldersNamed(Root, [TopazLayout.ModelsName])]);

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var survey = Survey(ct);

        switch (survey.Reach)
        {
            case TopazRootReach.Absent:
                return EmptyPlan("No Topaz Labs application has downloaded models on this computer.");

            case TopazRootReach.Refused:
                return UnreadableRootPlan(survey.Root);

            case TopazRootReach.Link:
                return UnexaminedPlan(LinkNote(survey.Root).Message) with
                {
                    ProtectedPaths = Protect((survey.Root, CacheLevelWalk.LinkReason)),
                };

            // Found by name, and a listing right is separate from a traverse right, so the sentence is
            // "could not list" rather than "could not reach".
            case TopazRootReach.Unlisted:
                return EmptyPlan(UnreadableRoot.WhyNothingWasPlanned(survey.Root)) with
                {
                    Notes = [UnreadableRoot.Note(survey.Root)],
                    HasUnreadableRoot = true,
                    ProtectedPaths = Protect((survey.Root, UnlistedReason)),
                };
        }

        var notes = new List<PlanNote>();
        var protect = new List<(string Path, string Reason)>
        {
            (survey.Root, "Topaz Labs' own folder must survive. Only downloaded models inside it are removed."),
        };
        var declined = 0;
        var unreadable = false;

        foreach (var link in survey.Links)
        {
            declined++;
            notes.Add(LinkNote(link));
            protect.Add((link, CacheLevelWalk.LinkReason));
        }

        foreach (var stranger in survey.Strangers)
        {
            const string Why = "not a folder Topaz names for one of its products.";

            declined++;
            notes.Add(new PlanNote(PlanNoteSeverity.Information, $"Leaving '{stranger}' alone: it is {Why}"));
            protect.Add((stranger, $"It is {Why}"));
        }

        var use = Use(survey);
        var (held, heldReason) = Held(survey, use, ct);

        if (held.Count > 0)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Warning, $"Left the models of products that may be installed alone. {heldReason}"));
        }

        List<DeletionTarget> targets = [];

        // Products that are gone first, so they are what the list opens with.
        foreach (var product in survey.Products.OrderBy(p => p.Standing is TopazInstallStanding.NotInstalled ? 0 : 1))
        {
            ct.ThrowIfCancellationRequested();

            protect.Add((product.Folder, $"{product.Name}'s own folder must survive. Only its downloaded models go."));
            protect.AddRange(product.Kept);

            if (product.Unreadable)
            {
                notes.Add(UnreadableRoot.Note(product.Folder));
                unreadable = true;
                declined++;
                continue;
            }

            if (product.Declined is { } refused)
            {
                declined++;
                notes.Add(new PlanNote(PlanNoteSeverity.Information, $"Leaving '{refused.Path}' alone: {refused.Reason}"));
                protect.Add(refused);
            }

            if (product.Target is not { } target || !DirectoryContent.MayBePresent(target))
            {
                continue;
            }

            if (held.Contains(product))
            {
                declined++;
                protect.Add((target, heldReason));
                continue;
            }

            targets.Add(Target(product, target, use, ct));
        }

        if (targets.Count == 0 && declined == 0)
        {
            return EmptyPlan("No Topaz Labs application has downloaded models on this computer.");
        }

        var (steps, measured) = await PlanDeletionsAsync(targets, keep, ct).ConfigureAwait(false);

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        if (BuildRunningProcessNote() is { } warning)
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
            // One entry per path, keeping the first reason given for it.
            ProtectedPaths = Protect([.. protect.DistinctBy(p => p.Path, StringComparer.OrdinalIgnoreCase)]),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = unreadable,
            WasNotExamined = targets.Count == 0 && declined > 0,
        };
    }

    private (List<TopazProduct> Held, string Reason) Held(TopazSurvey survey, CancellationToken ct) =>
        Held(survey, Use(survey), ct);

    /// <summary>
    /// The products whose models are held back while something may be loading them, and why: every
    /// one that may be installed. Nothing loads the models of a product that is gone.
    /// </summary>
    private static (List<TopazProduct> Held, string Reason) Held(TopazSurvey survey, TopazModelsUse use, CancellationToken ct)
    {
        List<TopazProduct> installed =
            [.. survey.Products.Where(p => p.Target is not null && p.Standing is not TopazInstallStanding.NotInstalled)];

        if (installed.Count == 0 || use.WhyInUse(ct) is not { } why)
        {
            return ([], string.Empty);
        }

        return (
            installed,
            $"Because {why}, and removing models a running program may have loaded would leave a part-removed set, they are "
            + "left alone. Close it, then scan again.");
    }

    private DeletionTarget Target(TopazProduct product, string target, TopazModelsUse use, CancellationToken ct)
    {
        var gone = product.Standing is TopazInstallStanding.NotInstalled;

        var reason = product.Standing switch
        {
            TopazInstallStanding.NotInstalled =>
                $"Machine-learning models {product.Name} downloaded. {product.Name} is no longer installed on "
                + "this computer, so nothing will load them again.",
            TopazInstallStanding.Installed =>
                $"Machine-learning models {product.Name} downloaded. It downloads each one again, through a "
                + "signed-in Topaz account, the next time a feature needs it.",
            _ =>
                $"Machine-learning models {product.Name} downloaded. Deguffer could not tell whether "
                + $"{product.Name} is still installed, so they are treated as an installed product's.",
        };

        return new DeletionTarget(
            target,
            reason,
            DirectoryAge.Of(target, ct),
            Identity: new ItemIdentity(product.Name, $"{product.Name} models"),
            Facets: [new ItemFacet("Installed", product.Standing switch
            {
                TopazInstallStanding.NotInstalled => "No",
                TopazInstallStanding.Installed => "Yes",
                _ => "Could not tell",
            })],
            Group: gone ? NotInstalledGroup : InstalledGroup,

            // A product that is gone has nothing running to wait for.
            UseCheck: gone ? null : use);
    }

    private static PlanNote LinkNote(string path) => new(
        PlanNoteSeverity.Information,
        $"Leaving '{path}' alone: it is a link to somewhere else, and Deguffer does not look through a link.");
}
