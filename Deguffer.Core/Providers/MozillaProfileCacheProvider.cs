using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The caches in the profiles of a Mozilla application: Firefox, and Thunderbird, which is built on
/// the same engine and keeps its profiles the same way. What differs between them is data, in
/// <see cref="MozillaApplication"/>, and each derived provider is that data and its own row's text.
///
/// <para><b>Mozilla splits a profile across two roots, and only one of them is ever touched.</b>
/// The roaming half under <c>%APPDATA%</c> holds the user's data — bookmarks and history for
/// Firefox, mail and address books for Thunderbird, and saved passwords and every preference for
/// both. The local half under <c>%LOCALAPPDATA%</c> holds the disk cache and the other regenerable
/// files. This provider plans against the local half alone, and asserts the roaming half survived
/// (§5.6).</para>
///
/// <para><b>Identification is positive, and it is <c>profiles.ini</c>.</b> A directory is a profile
/// because the application's own register names it, never because it is named like one or holds a
/// directory called <c>cache2</c>. <see cref="MozillaProfileDiscovery"/> answers that question, and
/// this provider is only ever asked what may go inside a directory that has already passed it — the
/// same separation, for the same reason, as <see cref="ChromiumUserDataProvider"/>'s.</para>
///
/// <para><b>The local path is derived rather than enumerated, so every segment of it is checked for
/// a link.</b> Elsewhere in Deguffer a target arrives from an enumeration that has already filtered
/// links out, and one reparse check on the directory itself completes the argument. Nothing
/// enumerates here: the whole path from <c>%LOCALAPPDATA%</c> to the profile is built from a text
/// file plus two constants, and relocating <c>%LOCALAPPDATA%\Mozilla</c> onto another volume with a
/// junction is a thing people do. A link anywhere along it would put the deletion on the far side
/// while every §5.6 survivor named below resolved through the same link and passed — the vacuous
/// negative. <see cref="DerivedPath.FirstObstacleBetween"/> is what stops that.</para>
///
/// <para>§5.1 does not apply. Neither application clears its cache from outside itself, and
/// Mozilla's own published advice for reclaiming the space is to delete <c>cache2</c>.</para>
///
/// <para>What Explore must refuse is <see cref="MozillaToolRoots"/>'s. What is left here is one
/// pass over the profiles, and its length is the cases it must report rather than skip: a register
/// nobody could read, a profile kept elsewhere, a link or a refusal on the derived path, and the
/// kept directory's size.</para>
/// </summary>
public abstract class MozillaProfileCacheProvider : CleanupProviderBase
{
    private const string LinkReason =
        "A link rather than a directory, so what it points at was never classified.";

    private readonly MozillaApplication _application;
    private readonly MozillaProfileDiscovery _discovery;
    private readonly MozillaToolRoots _toolRootDeclarations;
    private readonly PassMemo<IReadOnlyList<MozillaProfile>> _profiles = new();
    private readonly PassMemo<IReadOnlyList<ToolRoot>> _toolRoots = new();

    /// <summary>
    /// Every name the table declares, offerable or not, for the presence probe to look for.
    ///
    /// <para>The kept directory has to be here, and <see cref="DisposableChildSet.DisposableNames"/>
    /// excludes it by construction — which is right for that property and wrong for this question.
    /// A profile whose local half holds nothing but Firefox's <c>remote-settings</c> is exactly the
    /// case the size note exists for, it was four fifths of the profile measured, and a probe over
    /// the offerable names alone would report that profile absent. The planner would then never ask
    /// for the plan that reports it.</para>
    /// </summary>
    private readonly string[] _declaredNames;

    protected MozillaProfileCacheProvider(
        MozillaApplication application,
        IUserEnvironment? environment,
        IProcessRunner? runner,
        IProcessInspector? inspector,
        IDirectoryScanner? scanner)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner)
    {
        _application = application;
        _discovery = new MozillaProfileDiscovery(Environment, application.Directory);
        _toolRootDeclarations = new MozillaToolRoots(application, _discovery);
        _declaredNames = application.Kept is { } kept
            ? [.. application.Children.DisposableNames, kept.Name]
            : [.. application.Children.DisposableNames];
    }

    public sealed override SafetyTier Tier => SafetyTier.RegenerableCache;

    public sealed override StepGrain Grain => StepGrain.Parts;

    private string App => _application.Name;

    /// <summary>
    /// The profiles <c>profiles.ini</c> names, memoised for the life of a planning pass (G4).
    /// Presence, planning and <see cref="ToolRoots"/> all ask the same question of the same file.
    ///
    /// Exposed so tests can assert that no roaming profile is ever a target.
    /// </summary>
    public IReadOnlyList<MozillaProfile> Profiles(CancellationToken ct = default) =>
        _profiles.Get(() => _discovery.Discover(ct));

    /// <summary>
    /// §5.2 as §7.1 needs it read from outside, memoised with the profiles it is built from. See
    /// <see cref="MozillaToolRoots.Declared"/>.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
        _toolRoots.Get(() => _toolRootDeclarations.Declared(Profiles(), LocatedElsewhere()));

    /// <summary>
    /// A profile at the top of a drive or share. See <see cref="MozillaToolRoots.AtTheTopOfADrive"/>.
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default) =>
        Task.FromResult(_toolRootDeclarations.AtTheTopOfADrive(LocatedElsewhere(ct)));

    /// <summary>
    /// Each profile kept outside the application's folder that names a directory, with that
    /// directory.
    /// </summary>
    private IEnumerable<(string Name, string Path)> LocatedElsewhere(CancellationToken ct = default)
    {
        // Discovery is what fills the list, and it is memoised, so this costs nothing after the
        // first read of the pass.
        Profiles(ct);

        foreach (var profile in _discovery.ProfilesElsewhere)
        {
            if (profile.Path is { } path)
            {
                yield return (profile.Name, path);
            }
        }
    }

    /// <summary>The named user data in one roaming directory, for §5.6.</summary>
    private IEnumerable<(string Path, string Reason)> UserDataIn(string directory) =>
        _application.UserData.Select(data => (Path.Combine(directory, data.Name), data.Reason));

    /// <summary>
    /// §5.6 for the profiles kept outside the application's folder: each directory, and the user data
    /// inside it by name, for the reason <see cref="MozillaApplication.UserData"/> gives.
    /// </summary>
    private IEnumerable<(string Path, string Reason)> SurvivorsElsewhere() =>
        from profile in LocatedElsewhere()
        from survivor in UserDataIn(profile.Path)
            .Prepend((
                profile.Path,
                $"Your '{profile.Name}' {App} profile, kept outside {App}'s own folder. Deguffer "
                + "removes nothing from it."))
        select survivor;

    /// <summary>
    /// Each cache this row recognises, in the local half of every profile the register lists, read
    /// through the same discovery the plan reads, whether or not one is there today. The roaming
    /// half, with the user's data, and any kept directory beside the caches are never reached.
    /// </summary>
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>(
        [
            .. from profile in Profiles(ct)
               from name in _application.Children.DisposableNames
               select CleanedPlace.Whole(Path.Combine(profile.LocalPath, name)),
        ]);

    public override void InvalidateCaches()
    {
        _profiles.Forget();
        _toolRoots.Forget();
        base.InvalidateCaches();
    }

    /// <summary>
    /// Presence is something on disk this provider has a sentence about, never a profile existing.
    /// A profile that has been opened once and closed keeps a local half with nothing declared in
    /// it, and reporting that as a source would offer the user a row the plan has nothing to say
    /// about.
    ///
    /// <para><b>The two cases that are not a cache are here because the planner never asks an absent
    /// provider for a plan.</b> A <c>profiles.ini</c> that would not be read or that Windows would not
    /// describe, and a profile kept outside the application's folder, each have their own sentence in
    /// <see cref="BuildPlanAsync"/>, and both sentences are unreachable if this answers false. The row
    /// then reads "Not installed" about an application that is installed, which is a stronger untruth
    /// than the "Already clear" it would otherwise be. <see cref="_declaredNames"/> covers the third
    /// case, a profile holding the kept directory and no cache.</para>
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default)
    {
        var profiles = Profiles(ct);

        return Task.FromResult(
            _discovery.Register is MozillaRegisterReading.Unreached or MozillaRegisterReading.Unreadable
            || _discovery.ProfilesElsewhere.Count > 0
            || profiles.Any(profile => HasDeclaredContent(profile, ct)));
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var profiles = Profiles(ct);

        if (profiles.Count == 0)
        {
            if (_discovery.Register is MozillaRegisterReading.Unreached or MozillaRegisterReading.Unreadable)
            {
                var register = new UnreadFile(
                    _discovery.ProfilesPath,
                    Unreached: _discovery.Register is MozillaRegisterReading.Unreached);

                return UnreadableRootPlan(register.Path) with
                {
                    Notes =
                    [
                        new PlanNote(
                            PlanNoteSeverity.Warning,
                            register.Opening($"{App}'s list of profiles")
                            + $", so Deguffer could not work out which {App} profiles exist. Nothing was "
                            + "planned, and nothing was ruled out either."),
                    ],
                };
            }

            // Every profile there is sits outside the application's own folder. Saying "no profile"
            // here would be the same untruth as saying it about a register nobody could read.
            return _discovery.ProfilesElsewhere.Count > 0
                ? UnexaminedPlan(StoredElsewhere(_discovery.ProfilesElsewhere)) with
                {
                    ProtectedPaths = Protect([.. SurvivorsElsewhere()]),
                }
                : EmptyPlan($"{App} has no profile in this user's account.");
        }

        var notes = new List<PlanNote>();
        var targets = new List<DeletionTarget>();
        var declined = new List<(string Path, string Reason)>();
        var survivors = new List<(string Path, string Reason)>();
        var unreadable = false;

        survivors.Add((
            _discovery.RoamingRoot,
            $"{App}'s own folder, which holds the register of your profiles and the profiles "
            + "themselves. Nothing is ever removed from it."));
        survivors.Add((
            _discovery.ProfilesPath,
            $"{App}'s register of your profiles. Removing it would lose every profile."));
        survivors.Add((
            _discovery.LocalRoot,
            $"{App}'s cache folder itself must survive — only recognised caches inside a profile "
            + "under it are removed."));

        if (_discovery.ProfilesElsewhere.Count > 0)
        {
            // Not "we left them alone": nothing about them was established at all, and a plan that
            // implied otherwise would be claiming a folder is clear that was never looked at.
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                StoredElsewhere(_discovery.ProfilesElsewhere)));
            survivors.AddRange(SurvivorsElsewhere());
        }

        foreach (var profile in profiles)
        {
            ct.ThrowIfCancellationRequested();

            survivors.Add((
                profile.RoamingPath,
                $"Your '{profile.Name}' {App} profile. Deguffer removes nothing from it."));
            survivors.AddRange(UserDataIn(profile.RoamingPath));

            // Before the existence check, not after it. A link partway up the path resolves onto a
            // directory that holds no profile of its own, so the profile itself reads as absent and
            // the pass would end reporting nothing at all about a redirection it did detect. The
            // walk answers the refusal too, so a segment Windows would not describe stops the pass
            // here rather than being read as "no link, carry on".
            if (DerivedPath.FirstObstacleBetween(Environment.LocalAppData, profile.LocalPath) is { } obstacle)
            {
                if (obstacle.IsLink)
                {
                    notes.Add(LinkNote(obstacle.Path));
                    declined.Add((obstacle.Path, LinkReason));
                }
                else
                {
                    notes.Add(UnreadableRoot.UnreachedNote(obstacle.Path));
                    unreadable = true;
                }

                continue;
            }

            // Absence is all the walk above leaves: it stops on a link and on a refusal, and the
            // profile folder is its last segment.
            if (!LongPath.DirectoryExists(profile.LocalPath))
            {
                continue;
            }

            survivors.Add((
                profile.LocalPath,
                $"The cache folder for your '{profile.Name}' profile must survive — only recognised "
                + "caches inside it are removed."));

            var scan = ChildDirectories.Under(profile.LocalPath);

            if (scan.Unreadable)
            {
                notes.Add(UnreadableRoot.Note(profile.LocalPath));
                unreadable = true;
                continue;
            }

            foreach (var linked in scan.Links)
            {
                var path = LongPath.Display(linked.FullName);
                notes.Add(LinkNote(path));
                declined.Add((path, LinkReason));
            }

            var spared = 0;

            foreach (var child in scan.Directories)
            {
                var classification = _application.Children.Classify(child.Name);
                var path = LongPath.Display(child.FullName);

                if (classification.Tier.IsOfferable())
                {
                    targets.Add(new DeletionTarget(path, classification.Reason, Group: profile.Name));
                    continue;
                }

                survivors.Add((path, classification.Reason));
                spared++;

                if (_application.Kept is { } kept
                    && child.Name.Equals(kept.Name, StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add(await KeptNoteAsync(kept, path, ct).ConfigureAwait(false));
                }
            }

            // One note per profile rather than one per spared child. Each is still asserted
            // individually by §5.6, and this is the sentence that says so.
            if (spared > 0)
            {
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"In the '{profile.Name}' profile, {spared} other "
                    + $"{(spared == 1 ? "item is" : "items are")} left alone beside the caches."));
            }
        }

        // A note rather than an early return, because the pass has other things to report even when
        // it found no cache: the size of the kept directory it deliberately does not offer, and any
        // profile stored somewhere it will not examine. Returning an empty plan here would discard
        // both, and a profile holding 1.5 GB of remote-settings and nothing else would read as clear
        // with no account of the space.
        if (targets.Count == 0 && declined.Count == 0 && !unreadable)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"No {App} profile on this machine is keeping a cache on disk."));
        }

        var (steps, measured) = await PlanDeletionsAsync(targets, keep, ct).ConfigureAwait(false);

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        // §5.3, and only where something is actually going to be removed. A warning that the
        // application is holding files open, on a row with nothing to delete, describes a clean that
        // will not happen.
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
                [.. survivors.Concat(declined)]),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = unreadable,
            // A profile kept outside the application's folder counts here for the same reason a
            // declined link does: nothing was removed and something was never looked at, so the shell
            // must not call the row clear.
            WasNotExamined = targets.Count == 0
                && (declined.Count > 0 || _discovery.ProfilesElsewhere.Count > 0),
        };
    }

    /// <summary>
    /// The size of the kept directory, measured unguarded: nothing is being deleted, so withholding
    /// recent files would describe a deletion that is not happening.
    /// </summary>
    private async Task<PlanNote> KeptNoteAsync(MozillaKeptData kept, string path, CancellationToken ct)
    {
        var size = await Scanner
            .MeasureAsync(path, MinimumAge.Off, progress: null, ct: ct)
            .ConfigureAwait(false);

        // A folder Windows would not let the walk into measures zero, and "keeps 0 bytes" is a claim
        // about it that nobody checked.
        var held = size.WasReached
            ? $"{App} keeps {FreeSpace.Format(size.Size)} of {kept.What} in '{path}'"
            : $"{App} keeps {kept.What} in '{path}', which Windows would not let Deguffer measure";

        return new PlanNote(PlanNoteSeverity.Information, $"{held}, {kept.Why}");
    }

    /// <summary>
    /// The sentence for a profile <c>profiles.ini</c> puts outside the application's own folder.
    /// Said in two places, because such a profile can be one of several or the only one there is,
    /// and the second case returns before the plan is ever built.
    /// </summary>
    private string StoredElsewhere(IReadOnlyList<MozillaProfileElsewhere> profiles) =>
        $"The '{string.Join("' and '", profiles.Select(profile => profile.Name))}' "
        + $"{(profiles.Count == 1 ? "profile is" : "profiles are")} "
        + $"kept outside {App}'s own folder. Deguffer did not examine "
        + $"{(profiles.Count == 1 ? "it" : "them")}: a profile stored that way keeps its cache among "
        + $"your {_application.UserDataSummary} rather than separately from them.";

    private static PlanNote LinkNote(string path) => new(
        PlanNoteSeverity.Information,
        $"Leaving '{path}' alone: it is a link to somewhere else, and Deguffer does not delete "
        + "through a link.");

    /// <summary>
    /// Whether any declared name is on disk for this profile, by probing the table rather than by
    /// enumerating (G4). One existence check per declared name, and not one of them can reach a path
    /// the table does not name.
    ///
    /// <para><b>A presence probe, not a safety gate.</b> It answers through a junction, so a profile
    /// whose only cache is a link reports as present here and then yields no target, because
    /// <see cref="BuildPlanAsync"/> declines it. A future edit must not read a true from this as
    /// licence to delete anything.</para>
    /// </summary>
    private bool HasDeclaredContent(MozillaProfile profile, CancellationToken ct)
    {
        foreach (var name in _declaredNames)
        {
            ct.ThrowIfCancellationRequested();

            if (LongPath.DirectoryMayExist(Path.Combine(profile.LocalPath, name)))
            {
                return true;
            }
        }

        return false;
    }
}
