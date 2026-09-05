using System.Text.RegularExpressions;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// What the Squirrel updater unpacked and then failed to delete: its shared staging folder, and the
/// update packages an application's own index no longer names (466 MB of staging and 87 MB of spent
/// packages across three applications on the machine this was measured on).
///
/// <para><b>One cause, two places.</b> Squirrel unpacks an install or an update into
/// <c>%LOCALAPPDATA%\SquirrelTemp</c> and deletes what it unpacked when it is finished — in a
/// <c>using</c> block, so a process killed part-way through leaks the lot. It prunes each
/// application's <c>packages</c> folder to the one release its index names — in an unguarded loop,
/// so the first failure abandons the rest. Both leftovers are the same thing: work Squirrel's own
/// clean-up was supposed to do and did not.</para>
///
/// <para><b>The staging folder is shared by every Squirrel application on the machine, and that is
/// the hazard here.</b> Squirrel's maintainer gave it as the reason the library cannot clear the
/// folder itself: one application cannot know that another is using it right now. So this provider
/// asks whether anything is running from inside a staging directory before it offers it, and refuses
/// the ones that are, rather than warning about them (§5.3).</para>
///
/// <para><b>§5.1 does not apply.</b> <c>Update.exe</c> has no clean-up action — its whole command
/// set is install, uninstall, download, update, releasify, shortcut, deshortcut, process-start,
/// update-self and check-for-update. The only thing that clears either location is an update
/// running to completion, which is not a command Deguffer can issue on the user's behalf.</para>
/// </summary>
public sealed partial class SquirrelStagingProvider : CleanupProviderBase
{
    /// <summary>
    /// A staging directory Squirrel made: the literal prefix it writes, then <b>exactly one</b>
    /// character from the alphabet its name generator draws on — lowercase Latin, then Greek and
    /// Coptic, then Cyrillic, 360 characters in all.
    ///
    /// <para><b>One character rather than one or more, and that difference is the whole of §5.2
    /// here.</b> The generator hands out the first free name, so it produces a single-character
    /// suffix until 360 staging directories exist at once — <c>tempa</c>, <c>tempb</c>, and so on,
    /// which is what the framework's own bug reports show and what the measured machine held. A
    /// longer run of Latin letters cannot be told from an ordinary word: <c>temp</c> followed by one
    /// or more also claims <c>templates</c>, <c>temporary</c> and <c>tempdata</c>. Under the default
    /// root that costs nothing, because nothing else writes there. Under a <c>SQUIRREL_TEMP</c>
    /// pointed at a folder shared with anything else, it would offer somebody's directory as a
    /// pre-selected Tier 1 row. So a name past the 360th is left alone, which reclaims less and is
    /// the direction §5.2 requires being wrong in.</para>
    ///
    /// <para>Case-sensitive, deliberately. Squirrel generates these names in lowercase and Windows
    /// keeps the case a directory was created with, so matching case exactly recognises every folder
    /// Squirrel made and nothing else.</para>
    /// </summary>
    [GeneratedRegex(@"\Atemp[a-z\u03B0-\u03FE\u0400-\u04FE]\z", RegexOptions.CultureInvariant)]
    private static partial Regex StagingDirectory();

    private const string StagingReason =
        "An install or an update the Squirrel updater unpacked here and did not clear away "
        + "afterwards. Nothing reads it once the update has finished.";

    private const string PackageReason =
        "An update package this application's own index no longer refers to. Squirrel's clean-up "
        + "was supposed to remove it after the update that replaced it.";

    private const string StagingRootReason =
        "The staging folder itself must survive — only the directories Squirrel unpacked into it "
        + "are removed, and every Squirrel application on this machine shares it.";

    /// <summary>
    /// The files Squirrel and its bootstrapper leave in the staging folder, named in full rather
    /// than sampled.
    ///
    /// <para>Named at all because child classification enumerates directories, so anything that is a
    /// file is never seen, never classified and never asserted unless a provider names it. This
    /// provider removes directories from the folder those files sit in, so an assertion that the
    /// folder survived would pass with every one of them gone.</para>
    ///
    /// <para>Whichever is absent records itself as nothing to preserve rather than as a pass, so
    /// naming all three costs nothing on a machine that holds one.</para>
    /// </summary>
    private static readonly (string Name, string Reason)[] StagingRootFiles =
    [
        ("SquirrelSetup.log", "The record of what the updater installed on this machine, and when."),
        ("Squirrel-Install.log", "The record of what the updater installed on this machine, and when."),
        ("setup.json", "What an installer was told to run. It is not something the updater unpacked."),
    ];

    private readonly SquirrelDiscovery _discovery;
    private readonly ILiveTreeInspector _liveTrees;

    private IReadOnlyList<ToolRoot>? _toolRoots;

    public SquirrelStagingProvider(
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

    public override string Id => "squirrel-staging";

    public override string Name => "Squirrel updater leftovers";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override string WhatHappensOnNextUse =>
        "Nothing changes for any application. Each one still starts, still updates itself, and "
        + "still applies its next update as a patch rather than a whole download, because the "
        + "package it builds that patch from is left in place. The next install or update unpacks "
        + "itself into the staging folder again, exactly as it would have done.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "applications that update themselves through Squirrel, a Windows updater a "
            + "large family of desktop applications is built on",
        Publisher = "the Squirrel project, and whoever publishes each application using it",
        Purpose = "Squirrel unpacks an install or an update into a staging folder your whole "
            + "account shares, and keeps the packages it downloaded in a folder inside each "
            + "application. It deletes both when it has finished, and it misses often enough that "
            + "gigabytes accumulate — the framework's own issue tracker has reports of 35 GB in one "
            + "staging folder.",
        Recommendation = "Nothing here is yours, and no application still needs any of it. Deguffer "
            + "removes what Squirrel unpacked, and the packages an application's own index has "
            + "stopped referring to. It never removes the index, the package the next update is "
            + "built from, or a download in progress.",
    };

    /// <summary>
    /// §5.3. <c>Update.exe</c> is the updater every Squirrel application installs beside itself, and
    /// it is the process that writes both locations. The name is generic, and that is the right way
    /// to be wrong here: this produces a warning, never a refusal, and the refusal that matters is
    /// the live-directory check below it.
    /// </summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => ["Update", "Squirrel"];

    /// <summary>
    /// §5.2 as §7.1 needs it read from outside: the staging folder, whose unpacked directories may
    /// go, and each application's <c>packages</c> folder, where nothing may.
    ///
    /// <para>The packages folder recognises no child at all because what this provider removes from
    /// it is decided per file, from Squirrel's own index, and a declaration reads a name rather than
    /// an index. Explore therefore refuses the whole folder — which is narrower than what the
    /// Storage page offers, and narrow is the direction §5.2 requires.</para>
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots => _toolRoots ??= Declare();

    public override void InvalidateCaches()
    {
        _discovery.Invalidate();
        _liveTrees.Invalidate();
        _toolRoots = null;
        base.InvalidateCaches();
    }

    /// <summary>
    /// Present where there is a staging folder or a Squirrel application to talk about.
    ///
    /// <para>Deliberately not "there is something to remove". The planner never asks an absent
    /// provider for a plan, so a machine whose staging folder cannot be listed, or whose
    /// <see cref="SquirrelDiscovery.StagingVariable"/> names something that is not a path, would
    /// answer false here and the sentence explaining that would be unreachable. The row would then
    /// read "Not installed" about applications that are installed.</para>
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default)
    {
        var sweep = _discovery.Look(ct);

        return Task.FromResult(
            (_discovery.StagingRoot is { } root && LongPath.DirectoryExists(root))
            || _discovery.ConfiguredStagingRoot is not null
            || sweep.Installations.Count > 0
            || sweep.ApplicationDataUnreadable);
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        // Before anything reads what the sweep could not list. Those two facts are only known once
        // it has run, so a plan that read them off a lazily-memoised property would see the defaults
        // and report an application folder it was refused as though the refusal had not happened.
        var sweep = _discovery.Look(ct);

        var notes = new List<PlanNote>();
        var survivors = new List<(string Path, string Reason)>();
        var targets = new List<DeletionTarget>();

        var unreadable = false;
        var declined = 0;

        if (sweep.ApplicationDataUnreadable)
        {
            notes.Add(UnreadableRoot.Note(Environment.LocalAppData));
            unreadable = true;
        }

        foreach (var refused in sweep.UnreadableRoots)
        {
            notes.Add(UnreadableRoot.Note(refused));
            unreadable = true;
        }

        var staging = CollectStaging(notes, survivors, ct);

        targets.AddRange(staging.Targets);
        unreadable |= staging.Unreadable;
        declined += staging.Declined;

        var packages = CollectPackages(sweep.Installations, notes, survivors, ct);

        targets.AddRange(packages.Targets);
        unreadable |= packages.Unreadable;
        declined += packages.Declined;

        if (targets.Count == 0 && declined == 0 && !unreadable)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                "The Squirrel updater has left nothing behind on this machine."));
        }

        var (steps, measured) = await PlanDeletionsAsync(targets, keep, ct).ConfigureAwait(false);

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        // Only where something is actually going to be removed. A warning that an updater is
        // running, on a row with nothing to delete, describes a clean that will not happen.
        if (targets.Count > 0 && BuildRunningProcessNote() is { } warning)
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
                [.. survivors.DistinctBy(s => s.Path, StringComparer.OrdinalIgnoreCase)]),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = unreadable,
            WasNotExamined = targets.Count == 0 && declined > 0,
        };
    }

    /// <summary>
    /// The staging folder: what Squirrel unpacked into it, minus anything something is using.
    ///
    /// <para>The liveness check is a refusal rather than a warning, and it is the one safeguard this
    /// provider has that the others do not need. Every Squirrel application on the machine writes
    /// here, so a directory being removed can belong to an update somebody else's application is
    /// running right now — the collision Squirrel's maintainer named as the reason the library
    /// leaves the folder alone.</para>
    /// </summary>
    private Collected CollectStaging(
        List<PlanNote> notes,
        List<(string Path, string Reason)> survivors,
        CancellationToken ct)
    {
        if (_discovery.StagingRoot is not { } root)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"{SquirrelDiscovery.StagingVariable} is set to '{_discovery.ConfiguredStagingRoot}', "
                + "which is not a full path. Deguffer cannot tell which folder that means, so it is "
                + "leaving the updater's staging folder alone."));

            return new Collected([], Unreadable: false, Declined: 1);
        }

        if (!LongPath.DirectoryExists(root))
        {
            return new Collected([], Unreadable: false, Declined: 0);
        }

        // The root arrives by name, from an environment variable or a constant, so nothing has
        // classified it. A junctioned root hands back the far side's ordinary directories, and a
        // recognised name among them would be removed while every survivor named for this root
        // resolved through the same link and passed — the vacuous negative.
        if (LongPath.IsReparsePoint(root))
        {
            notes.Add(CacheLevelWalk.Note(root));
            survivors.Add((root, CacheLevelWalk.LinkReason));

            return new Collected([], Unreadable: false, Declined: 1);
        }

        survivors.Add((root, StagingRootReason));
        survivors.AddRange(StagingRootFiles.Select(f => (Path.Combine(root, f.Name), f.Reason)));

        var scan = ChildDirectories.Under(root);

        if (scan.Unreadable)
        {
            notes.Add(UnreadableRoot.Note(root));
            return new Collected([], Unreadable: true, Declined: 0);
        }

        var declined = 0;

        foreach (var link in scan.Links)
        {
            var path = LongPath.Display(link.FullName);

            notes.Add(CacheLevelWalk.Note(path));
            survivors.Add((path, CacheLevelWalk.LinkReason));
            declined++;
        }

        var candidates = new List<RecognisedBuildDirectory>();

        foreach (var child in scan.Directories)
        {
            ct.ThrowIfCancellationRequested();

            var path = LongPath.Display(child.FullName);

            if (StagingDirectory().IsMatch(child.Name))
            {
                candidates.Add(new RecognisedBuildDirectory(path, root));
            }
            else
            {
                // §5.2: whatever else has collected here is not Squirrel's unpacked staging, so it
                // is asserted to survive rather than merely omitted. The files beside these are
                // named separately — child classification never sees one.
                survivors.Add((
                    path,
                    "Not something the Squirrel updater unpacked, so it is left alone."));
            }
        }

        var live = LiveTreeVeto.Apply(_liveTrees, candidates, lockFiles: [], ct);

        foreach (var vetoed in live.Vetoed)
        {
            survivors.Add((vetoed.Directory, LiveTreeVeto.ProtectedReason));
        }

        if (live.Vetoed.Count > 0)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Warning,
                $"Left {live.Vetoed.Count} staging "
                + (live.Vetoed.Count == 1 ? "directory" : "directories")
                + " alone: an application is installing or updating through "
                + (live.Vetoed.Count == 1 ? "it" : "them")
                + " right now. Every Squirrel application shares this folder."));
        }

        if (!live.Complete)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Warning,
                "Deguffer could not check whether an application is installing or updating right "
                + "now. Every Squirrel application shares this folder, so finish any install "
                + "before cleaning it."));
        }

        return new Collected(
            [.. live.Cleared.Select(c => new DeletionTarget(c.Path, StagingReason, DirectoryAge.Of(c.Path, ct)))],
            Unreadable: false,
            // A held-back directory counts here for the same reason a declined link does: something
            // real was left unexamined. Without it, a machine whose every staging directory is busy
            // — the single-directory machine, which is the ordinary one — produces no steps and no
            // declines, so the row renders as "Already clear" and the plan carries a sentence saying
            // the updater left nothing behind, beside the warning saying the opposite.
            declined + live.Vetoed.Count);
    }

    /// <summary>
    /// Each application's <c>packages</c> folder, against its own index. The index, the staged-user
    /// identifier beside it and every package the index still names are asserted to survive; see
    /// <see cref="SquirrelPackages"/> for why the folder is never taken whole.
    /// </summary>
    private Collected CollectPackages(
        IReadOnlyList<SquirrelInstallation> installations,
        List<PlanNote> notes,
        List<(string Path, string Reason)> survivors,
        CancellationToken ct)
    {
        var targets = new List<DeletionTarget>();
        var unreadable = false;
        var declined = 0;
        var indexesUnread = 0;
        var unordered = 0;

        foreach (var installation in installations)
        {
            ct.ThrowIfCancellationRequested();

            var packages = Path.Combine(installation.Root, SquirrelDiscovery.PackagesDirectoryName);

            survivors.Add((
                installation.Root,
                $"The folder {installation.Name} is installed in must survive — only spent update "
                + "packages inside it are removed."));
            survivors.Add((
                Path.Combine(installation.Root, SquirrelDiscovery.UpdaterName),
                $"The updater {installation.Name} keeps itself up to date with."));

            // The builds are siblings of the folder this provider reaches into, which is exactly
            // when an over-broad rule takes one with the other — an assertion that the application's
            // folder survived would pass with every build inside it gone. Removing one is the
            // superseded-versions provider's business and never this one's, at any version.
            survivors.AddRange(installation.Versions.Select(version => (
                version.Path,
                $"A build of {installation.Name}. This row removes update packages and never a "
                + "build.")));

            if (!LongPath.DirectoryExists(packages))
            {
                continue;
            }

            if (LongPath.IsReparsePoint(packages))
            {
                notes.Add(CacheLevelWalk.Note(packages));
                survivors.Add((packages, CacheLevelWalk.LinkReason));
                declined++;
                continue;
            }

            survivors.Add((
                packages,
                $"The folder {installation.Name} keeps its update packages in must survive — only "
                + "the packages it no longer refers to are removed."));
            survivors.Add((
                Path.Combine(packages, SquirrelPackages.IndexName),
                $"{installation.Name}'s own record of the packages it holds. Its shortcut reads this "
                + "file to work out which version to start."));
            survivors.Add((
                Path.Combine(packages, ".betaId"),
                $"The identifier that decides whether this computer gets {installation.Name}'s "
                + "staged releases early."));

            // An installation nobody could order is settled here rather than inside the reading,
            // because the reason belongs to the caller. Deciding whether a package is spent means
            // comparing it against the installed build, and there is no installed build to compare
            // against — which is a different fact from an index that would not be read, and folding
            // the two together put a sentence about an unreadable record in front of a user whose
            // record was perfectly readable.
            if (installation.Current is not { } current)
            {
                unordered++;
                declined++;
                continue;
            }

            var reading = SquirrelPackages.Read(packages, current.Number, ct);

            survivors.AddRange(reading.StillNeeded);

            if (reading.DirectoryUnreadable)
            {
                notes.Add(UnreadableRoot.Note(packages));
                unreadable = true;
                continue;
            }

            if (reading.IndexUnreadable)
            {
                indexesUnread++;
                declined++;
                continue;
            }

            foreach (var link in reading.Declined)
            {
                notes.Add(CacheLevelWalk.Note(link));
                declined++;
            }

            targets.AddRange(reading.Superseded.Select(path => new DeletionTarget(
                path,
                PackageReason,
                LastWritten(path),
                TargetKind.File)));
        }

        if (indexesUnread > 0)
        {
            // One sentence for all of them. Which application it was does not change what the user
            // can do about it, and a line per application would bury the rest of the plan on a
            // machine with several.
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"Left the update packages of {indexesUnread} "
                + (indexesUnread == 1 ? "application" : "applications")
                + " alone: Deguffer could not read the record of which packages "
                + (indexesUnread == 1 ? "it" : "they")
                + " still needs, and without it a spent package cannot be told from the one the "
                + "next update is built from."));
        }

        if (unordered > 0)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"Left the update packages of {unordered} "
                + (unordered == 1 ? "application" : "applications")
                + " alone: Deguffer could not work out which build "
                + (unordered == 1 ? "is" : "are")
                + " installed, and a spent package is only spent by comparison with it."));
        }

        return new Collected(targets, unreadable, declined);
    }

    /// <summary>
    /// A package's own timestamp, which is when it was downloaded — the age §7 shows beside the row.
    ///
    /// <para>Its own rather than <see cref="DirectoryAge"/>'s, because the subject is a file. The
    /// staging directories beside these carry an age too, and a provider whose rows carried one on
    /// half its steps would leave the column blank for no reason the user could see.</para>
    /// </summary>
    private static DateTime? LastWritten(string path)
    {
        try
        {
            return new FileInfo(LongPath.Extended(path)).LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // §5.3 makes a refusal ordinary, and §7 renders a null as unknown rather than as an age
            // — which is the honest rendering, since an age is what invites a deletion.
            return null;
        }
    }

    private IReadOnlyList<ToolRoot> Declare()
    {
        var roots = new List<ToolRoot>();

        if (_discovery.StagingRoot is { } staging)
        {
            roots.Add(new ToolRoot(
                staging,
                "This is the folder the Squirrel updater unpacks installs and updates into, and "
                + "every application on this machine that uses Squirrel shares it. Deguffer removes "
                + "the directories it unpacked and nothing else.",
                name => StagingDirectory().IsMatch(name)));
        }

        roots.AddRange(_discovery.Look().Installations.Select(installation => ToolRoot.Of(
            Path.Combine(installation.Root, SquirrelDiscovery.PackagesDirectoryName),
            $"This is where {installation.Name} keeps the packages it updates itself from, and its "
            + "shortcut reads the index in here to work out which version to start. Spent packages "
            + "are removed from the Storage page, where Deguffer knows which of them the "
            + "application has stopped referring to.",
            new DisposableChildSet([]))));

        return roots;
    }

    /// <summary>What one of the two locations came to, so the plan can add the halves together.</summary>
    /// <param name="Targets">What may be removed.</param>
    /// <param name="Unreadable">Whether a folder refused to be listed, so its content is unknown.</param>
    /// <param name="Declined">
    /// How many locations were passed over for a reason of Deguffer's own — a link, an index it
    /// could not read, a configured path it could not resolve. Counted because a plan with no steps
    /// and a decline must not be rendered as "Already clear".
    /// </param>
    private readonly record struct Collected(
        IReadOnlyList<DeletionTarget> Targets,
        bool Unreadable,
        int Declined);
}
