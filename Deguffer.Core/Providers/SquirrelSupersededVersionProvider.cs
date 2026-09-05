using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The builds a Squirrel application replaced and left on disk (719 MB for one application on the
/// machine this was measured on, beside the 722 MB it is actually running).
///
/// <para><b>These are not a rollback copy, and establishing that was the whole of the work.</b>
/// Squirrel's own clean-up calls them dead — "we assume previous versions in the directory are
/// already uninstalled, but not deleted, and we blow them away" — and deletes every version
/// directory except the new one and the one it replaced. That exclusion is why two survive rather
/// than one, and the vendor's own documentation says so: "the current and immediately previous
/// version of your application are not deleted on clean up". The next update removes the one left
/// over. There is no rollback in Squirrel to consume it: the framework's documentation states
/// plainly that going back to a previous version is not supported, and there is no code that
/// does it.</para>
///
/// <para><b>Nothing points into one after an update.</b> The shim in the application's own folder
/// picks the highest version at launch, every time — no path is recorded anywhere. The updater is a
/// copy in that folder rather than in a version directory. A shortcut targets the shim, and the
/// pinned ones are re-pointed at the new version while the update runs.</para>
///
/// <para><b>Tier 2 rather than Tier 1, and the price is named rather than waved away.</b> A cache
/// refills itself; a build does not, and there is no supported way to get an old one back. Squirrel
/// also runs the application's own <c>--squirrel-obsolete</c> hook against a version before it
/// deletes it, which is where an application deregisters what that build registered. Removing the
/// directory here means that hook never runs, and Deguffer does not run it — starting a vendor's
/// executable to tidy up after a deletion is not something this tool does. So the row is offered,
/// never pre-selected, and §7's acknowledgement applies.</para>
///
/// <para><b>An application that is running is refused outright, not warned about (§5.3).</b>
/// Squirrel's own clean-up skips a version directory a process is running from, and it is doing
/// less than this does: an application launched from an old build has that build open, and an
/// application launched from the new one may still be reading the folder beside it while it
/// finishes an update. The refusal covers the whole installation rather than one directory in
/// it.</para>
/// </summary>
public sealed class SquirrelSupersededVersionProvider : CleanupProviderBase
{
    private readonly SquirrelDiscovery _discovery;
    private readonly ILiveTreeInspector _liveTrees;

    private IReadOnlyList<ToolRoot>? _toolRoots;

    public SquirrelSupersededVersionProvider(
        IUserEnvironment? environment = null,
        SquirrelDiscovery? discovery = null,
        ILiveTreeInspector? liveTrees = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
    {
        _discovery = discovery ?? new SquirrelDiscovery(Environment);
        _liveTrees = liveTrees ?? LiveTreeInspector.Default;
    }

    public override string Id => "squirrel-superseded-versions";

    public override string Name => "Superseded application versions";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override string WhatHappensOnNextUse =>
        "Every application still starts, and starts the version you are using now — its shortcut "
        + "picks the newest build in the folder each time. What you give up is the build it "
        + "replaced: there is no supported way back to it, and its own tidy-up step, which the "
        + "updater would have run at the next update, does not run. Your settings, your sign-ins "
        + "and anything the application saved are somewhere else entirely and are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "applications that update themselves through Squirrel, a Windows updater a "
            + "large family of desktop applications is built on",
        Publisher = "the Squirrel project, and whoever publishes each application using it",
        Purpose = "A Squirrel application installs each version into a folder of its own and "
            + "launches whichever is newest. When it updates, it deletes the older builds — but "
            + "never the one it has just replaced, so a full second copy of the application sits "
            + "beside the one you use until the update after next.",
        Recommendation = "The application does not use these and cannot go back to them, and its "
            + "updater deletes them itself at the next update. They are offered rather than "
            + "cleaned for you because a build is not a cache: once one is gone there is no way "
            + "to get that version back.",
    };

    /// <summary>
    /// §5.2 as §7.1 needs it read from outside: each application's own folder, whose superseded
    /// version directories may go and where nothing else may.
    ///
    /// <para>Without it, Explore would let somebody delete an installed application out of the size
    /// picture — the folder, the updater, or the build it is running — while the Storage page was
    /// carefully leaving all three alone. Nothing else refuses it: an application folder under
    /// <c>%LOCALAPPDATA%</c> is an ordinary directory until a provider says whose it is.</para>
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots => _toolRoots ??=
    [
        .. _discovery.Installations.Select(installation =>
        {
            var superseded = new HashSet<string>(
                installation.Superseded.Select(v => v.Name), StringComparer.OrdinalIgnoreCase);

            return new ToolRoot(
                installation.Root,
                $"This is where {installation.Name} is installed. The build it runs, the updater "
                + "that keeps it current and the packages it updates from are all in here, and "
                + "Deguffer removes none of them.",
                superseded.Contains);
        }),
    ];

    public override void InvalidateCaches()
    {
        _discovery.Invalidate();
        _liveTrees.Invalidate();
        _toolRoots = null;
        base.InvalidateCaches();
    }

    /// <summary>
    /// Present where a Squirrel application was found, or where the profile would not be listed.
    ///
    /// <para>Not "there is a superseded version to remove": the planner never asks an absent
    /// provider for a plan, so the sentences about an installation held back and about a version
    /// nobody could order would be unreachable, and the row would read "Not installed" about
    /// applications that are.</para>
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(_discovery.Installations.Count > 0 || _discovery.ApplicationDataUnreadable);

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var notes = new List<PlanNote>();
        var survivors = new List<(string Path, string Reason)>();

        var unreadable = _discovery.ApplicationDataUnreadable;

        if (unreadable)
        {
            notes.Add(UnreadableRoot.Note(Environment.LocalAppData));
        }

        foreach (var refused in _discovery.UnreadableRoots)
        {
            notes.Add(UnreadableRoot.Note(refused));
            unreadable = true;
        }

        var installations = _discovery.Installations;

        foreach (var installation in installations)
        {
            ct.ThrowIfCancellationRequested();
            Preserve(installation, survivors);
        }

        var unordered = installations.Where(i => i.UnreadableVersionNames.Count > 0).ToList();

        if (unordered.Count > 0)
        {
            // Said out loud rather than left as a smaller number. Every version of such an
            // application is left alone, including ones that really are superseded, and a plan that
            // was quiet about it would disagree with a folder the user can see two builds in.
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"Left every version of {Join([.. unordered.Select(i => i.Name)])} alone: "
                + (unordered.Count == 1 ? "it has" : "they have")
                + " a build whose version number Deguffer could not read, so it cannot tell which "
                + "one is in use."));
        }

        // One candidate per installation, not one per version directory. The question that decides
        // this is whether the application is running at all, and the process holding it open is
        // running from the build it did *not* supersede — so asking about the superseded directory
        // alone would answer no every time.
        var live = LiveTreeVeto.Apply(
            _liveTrees,
            [.. installations.Where(i => i.Superseded.Count > 0)
                .Select(i => new RecognisedBuildDirectory(i.Root, i.Root))],
            lockFiles: [],
            ct);

        var held = new HashSet<string>(
            live.Vetoed.Select(v => v.Directory), StringComparer.OrdinalIgnoreCase);

        foreach (var installation in installations.Where(i => held.Contains(i.Root)))
        {
            foreach (var version in installation.Superseded)
            {
                survivors.Add((version.Path, LiveTreeVeto.ProtectedReason));
            }
        }

        if (live.Vetoed.Count > 0)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Warning,
                $"Left the older builds of {Join([.. installations.Where(i => held.Contains(i.Root)).Select(i => i.Name)])} "
                + "alone: "
                + (live.Vetoed.Count == 1 ? "it is" : "they are")
                + " running. Close "
                + (live.Vetoed.Count == 1 ? "it" : "them")
                + " and preview again to include the builds."));
        }

        if (!live.Complete)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Warning,
                "Deguffer could not check whether these applications are running. Close them before "
                + "removing an older build."));
        }

        var targets =
            (from installation in installations
             where !held.Contains(installation.Root)
             from version in installation.Superseded
             select new DeletionTarget(
                 version.Path,
                 $"{installation.Name} {version.Number}, the build version "
                 + $"{installation.Current!.Number} replaced.",
                 DirectoryAge.Of(version.Path, ct)))
            .ToList();

        if (targets.Count == 0 && live.Vetoed.Count == 0 && unordered.Count == 0 && !unreadable)
        {
            notes.Add(installations.Count == 0
                ? new PlanNote(
                    PlanNoteSeverity.Information,
                    "No application on this machine updates itself through Squirrel.")
                : new PlanNote(
                    PlanNoteSeverity.Information,
                    "Every application that updates itself through Squirrel is holding one build "
                    + "and no more."));
        }

        var (steps, measured) = await PlanDeletionsAsync(targets, keep, ct).ConfigureAwait(false);

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            ProtectedPaths = Protect(
                [.. survivors.DistinctBy(s => s.Path, StringComparer.OrdinalIgnoreCase)]),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = unreadable,
            // A held-back installation and one nobody could order are both cases where something
            // real was left unexamined, so the shell must not call a row with no steps clear.
            WasNotExamined = targets.Count == 0 && (live.Vetoed.Count > 0 || unordered.Count > 0),
        };
    }

    /// <summary>
    /// §5.6 for one installation: the folder, the updater, the build it runs, the packages beside
    /// it, and any version directory whose number could not be read.
    ///
    /// <para>Each is named rather than covered by an assertion on the folder above it, because an
    /// assertion that the application's folder survived would pass with the running build gone —
    /// which is the whole of what this provider must never do.</para>
    /// </summary>
    private static void Preserve(
        SquirrelInstallation installation,
        List<(string Path, string Reason)> survivors)
    {
        survivors.Add((
            installation.Root,
            $"The folder {installation.Name} is installed in must survive — only builds it has "
            + "replaced are removed."));

        survivors.Add((
            Path.Combine(installation.Root, SquirrelDiscovery.UpdaterName),
            $"The updater {installation.Name} keeps itself up to date with, and the program its "
            + "shortcut runs."));

        survivors.Add((
            Path.Combine(installation.Root, SquirrelDiscovery.PackagesDirectoryName),
            $"The packages {installation.Name} updates itself from, and its record of them."));

        if (installation.Current is { } current)
        {
            survivors.Add((current.Path, $"The build of {installation.Name} you are using."));
        }

        survivors.AddRange(installation.UnreadableVersionNames.Select(name => (
            Path.Combine(installation.Root, name),
            $"Deguffer could not read a version number out of '{name}', so it cannot tell whether "
            + "this build is the one in use.")));
    }

    /// <summary>
    /// Application names for a sentence, in the form a reader expects rather than comma-separated
    /// throughout. Driving the real window is what catches a sentence that reads correctly only on
    /// a machine with one of something.
    /// </summary>
    private static string Join(IReadOnlyList<string> names) => names.Count switch
    {
        0 => string.Empty,
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };
}
