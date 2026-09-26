using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// What the two RetroArch rows share: finding each copy, naming what survives beside it, holding
/// everything back while RetroArch runs, and telling Explore the way to what is offered. Each row
/// states only which folders it reads and what in them it takes.
///
/// <para><b>Two rows because they are two tiers.</b> The shader and database sets are downloaded again
/// through a named menu entry, which is Tier 2. The thumbnails can hold art the user added by hand for
/// a game the libretro server does not carry, which nothing tells apart from what was downloaded, so
/// they are Tier 3. A plan carries one tier.</para>
///
/// <para><b>§5.1 has nothing to prefer.</b> RetroArch's online updater downloads each set again, and
/// nothing in it removes one.</para>
///
/// <para><b>Nothing of RetroArch's while it runs (§5.3).</b> It writes thumbnails as a playlist is
/// browsed and extracts an update in place, so while it is in the process table everything is held
/// back and refused in Explore, and the clean asks again before each removal.</para>
/// </summary>
public abstract class RetroArchProviderBase : CleanupProviderBase
{
    /// <summary>
    /// What §5.2 names in a copy's folder, relative to the program, as name and reason. Named because
    /// the rows reach their folders by setting and list nothing else, so none of this is ever
    /// enumerated.
    /// </summary>
    private static readonly (string Name, string Reason)[] ProtectedNames =
    [
        ("system", "BIOS and system files you supplied. Most cannot be downloaded at any price."),
        ("saves", "Your in-game saves."),
        ("states", "Your save states."),
        ("playlists", "Your playlists."),
        (Path.Combine("playlists", "logs"), "How long you have played each game. It is a record, not a log."),
        ("config", "The settings you chose for each core and game."),
        (Path.Combine("config", "remaps"), "Your controller remaps."),
        (Path.Combine("config", "record"), "Your recording settings."),
        ("screenshots", "Your screenshots."),
        ("recordings", "Your recordings."),
        ("downloads", "Content you downloaded through RetroArch."),
        (Path.Combine("downloads", "core_backups"), "Backups of your cores."),
        ("cheats", "Cheats, where the updater's files and your own sit together."),
        ("autoconfig", "Controller profiles, where the updater's files and your own sit together."),
        ("overlays", "Overlays and the bezels you added."),
        (Path.Combine("overlays", "keyboards"), "On-screen keyboards."),
        ("filters", "Audio and video filters."),
        ("assets", "The menu's look, and your wallpapers."),
        (Path.Combine("assets", "wallpapers"), "Your wallpapers."),
        ("cores", "The cores you installed. Each is installed again only by hand."),
        ("info", "What RetroArch knows about each core."),
        ("database", "RetroArch's database folder. Only the downloaded game databases are removed."),
        (Path.Combine("database", "cursors"), "Queries you saved against the game databases."),
        ("shaders", "RetroArch's shader folder, with the presets you saved. Only the downloaded shader sets are removed."),
        (RetroArchInstall.SettingsFileName, "RetroArch's settings."),
        ("retroarch-core-options.cfg", "The options you chose for each core."),
        ("custom.ini", "RetroArch's own settings file."),
    ];

    private readonly RetroArchDiscovery _discovery;
    private readonly RunningProcessCheck _stillClosed;
    private RetroArchReading? _reading;

    private protected RetroArchProviderBase(
        IUserEnvironment? environment,
        IProcessRunner? runner,
        IProcessInspector? inspector,
        IDirectoryScanner? scanner,
        RetroArchDiscovery? discovery)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
    {
        _discovery = discovery ?? new RetroArchDiscovery(Environment);
        _stillClosed = new RunningProcessCheck(Inspector, RetroArchInstall.ProcessNames);
        _discovery.Enlist(this);
    }

    public sealed override StepGrain Grain => StepGrain.Parts;

    /// <summary>What this row takes, as a plural noun phrase in lower case, such as "thumbnails".</summary>
    private protected abstract string Taken { get; }

    /// <summary>Read what this row takes for one copy of RetroArch into <paramref name="reading"/>.</summary>
    private protected abstract void Read(RetroArchInstall install, RetroArchReading reading, CancellationToken ct);

    public override void InvalidateCaches()
    {
        _reading = null;
        _discovery.Invalidate();
        base.InvalidateCaches();
    }

    private RetroArchReading Examine(CancellationToken ct)
    {
        if (_reading is { } kept)
        {
            return kept;
        }

        var finding = _discovery.Find(ct);
        var reading = RetroArchReading.From(finding.Found);

        foreach (var install in finding.Installs)
        {
            ct.ThrowIfCancellationRequested();

            if (install.Program is { } program)
            {
                reading.Survivors.Add((program, $"RetroArch's own folder. Only the {Taken} inside it are removed."));
                reading.Survivors.AddRange(ProtectedNames.Select(p => (Path.Combine(program, p.Name), p.Reason)));
            }

            Read(install, reading, ct);
        }

        return _reading = reading;
    }

    /// <summary>
    /// The folder <paramref name="setting"/> names for <paramref name="install"/>, or null where there is
    /// none, it could not be worked out, or it must be left alone, each of the last two said.
    /// </summary>
    /// <param name="what">What the folder holds, as a noun phrase in lower case, for the sentence.</param>
    private protected string? Locate(RetroArchInstall install, RetroArchFolderSetting setting, RetroArchReading reading, string what)
    {
        var folder = install.Folder(setting, Environment);

        if (folder.Unresolved is { } unresolved)
        {
            reading.Notes.Add(new PlanNote(PlanNoteSeverity.Information, $"Deguffer did not look for RetroArch's {what}: {unresolved}"));
            return null;
        }

        if (folder.Path is not { } path)
        {
            return null;
        }

        if (install.Program is { } program && LongPath.Contains(program, path))
        {
            // Built from the program's folder, so a link at any folder between would put the removal
            // somewhere nothing established.
            if (DerivedPath.FirstObstacleBetween(program, path) is { } obstacle)
            {
                if (obstacle.IsLink)
                {
                    reading.DeclineLink(obstacle.Path);
                }
                else
                {
                    reading.Unreached(obstacle.Path);
                }

                return null;
            }

            return path;
        }

        if (_discovery.WhyNotOwned(path) is { } why)
        {
            reading.Notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"Leaving '{path}' alone although RetroArch's settings name it for its {what}: {why}"));
            return null;
        }

        return path;
    }

    /// <summary>The folder Explore is told the way down from: the program's, where the folder is in it.</summary>
    private protected static string TopOf(RetroArchInstall install, string folder) =>
        install.Program is { } program && LongPath.Contains(program, folder) ? program : folder;

    /// <summary>
    /// Present where something is offered, or where the plan has something to say: a link left alone,
    /// something unread, or a folder that could not be worked out. A RetroArch that has downloaded
    /// none of this row's sets is not presence.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default)
    {
        var reading = Examine(ct);

        return Task.FromResult(
            reading.Targets.Count > 0 || reading.Declined.Count > 0 || reading.Unreadable || reading.Notes.Count > 0);
    }

    /// <summary>
    /// §5.2 as §7.1 reads it, at every level from the program's folder to what is offered, declared
    /// for every RetroArch row sharing this finding rather than for this one alone.
    ///
    /// <para><b>One declaration for both rows, one root for each folder.</b> Explore asks each root a
    /// provider discovers on its own and lets any one of them refuse, so two rows that each declared
    /// only their own way down would refuse each other's: the program's folder would refuse
    /// <c>thumbnails</c> on one row's word and <c>shaders</c> on the other's. So every row declares the
    /// same roots, built from what all of them read, and each folder is one root recognising everything
    /// any of them recognised in it. That also joins a folder read outside the program with the level
    /// the way down gives it, which on its own would recognise nothing.</para>
    ///
    /// <para>While RetroArch runs nothing is recognised. Every other path a plan names as protected is
    /// refused outright.</para>
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default)
    {
        var readings = _discovery.Rows.Select(row => row.Examine(ct)).ToList();
        var folders = readings.SelectMany(reading => reading.Folders).ToList();
        IEnumerable<ToolRoot> declared;

        if (IsRunning())
        {
            declared = folders
                .SelectMany(folder => new[] { folder.Top, folder.Path })
                .Select(path => new ToolRoot(path, HeldReason, static _ => false));
        }
        else
        {
            declared = folders
                .GroupBy(folder => folder.Top, StringComparer.OrdinalIgnoreCase)
                .SelectMany(top => ToolRoot.WayDown(top.Key, top.Select(folder => folder.Path), ProgramReason))
                .Concat(folders.Select(folder => new ToolRoot(folder.Path, FolderReason, folder.Recognised.Contains)));
        }

        var roots = declared
            .GroupBy(root => root.Path, StringComparer.OrdinalIgnoreCase)
            .Select(Joined)
            .ToList();
        var paths = roots.Select(root => root.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        roots.AddRange(readings
            .SelectMany(reading => reading.Survivors)
            .Where(survivor => !paths.Contains(survivor.Path))
            .DistinctBy(survivor => survivor.Path, StringComparer.OrdinalIgnoreCase)
            .Select(survivor => new ToolRoot(survivor.Path, survivor.Reason, static _ => false)));

        return Task.FromResult<IReadOnlyList<ToolRoot>>(roots);
    }

    private const string ProgramReason =
        "This is RetroArch's folder, which holds your saves, BIOS files and settings beside what its "
        + "online updater downloaded. Deguffer removes only those downloads.";

    private const string FolderReason =
        "This is a RetroArch folder. Deguffer removes only what RetroArch downloaded into it.";

    /// <summary>The roots declared for one folder, as one root recognising what any of them does.</summary>
    private static ToolRoot Joined(IGrouping<string, ToolRoot> folder)
    {
        var roots = folder.ToList();

        return roots is [var only]
            ? only
            : new ToolRoot(folder.Key, roots[0].Reason, name => roots.Exists(root => root.Recognises(name)));
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var reading = Examine(ct);
        var notes = new List<PlanNote>(reading.Notes);
        var held = IsRunning() && reading.Targets.Count > 0;

        if (held)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Warning, $"Left RetroArch's {Taken} alone: {HeldReason}"));
        }

        var (steps, measured) = await PlanDeletionsAsync(
            held
                ? []
                :
                [
                    .. reading.Targets.Select(target => new DeletionTarget(
                        target.Path,
                        target.Reason,
                        Kind: target.Kind,
                        Group: target.Group,
                        UseCheck: _stillClosed)),
                ],
            keep,
            ct).ConfigureAwait(false);

        if (steps.Count == 0 && !held && reading.Declined.Count == 0 && !reading.Unreadable)
        {
            return EmptyPlan($"No RetroArch {Taken} were found beside a RetroArch program or in a folder its settings name.")
                with { Notes = notes };
        }

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
            // Held first, so a folder is named for why it was held rather than as a folder only part of
            // which is taken.
            ProtectedPaths = Protect(
            [
                .. reading.Folders
                    .Where(_ => held)
                    .Select(folder => (folder.Path, Reason: HeldReason))
                    .Concat(reading.Survivors)
                    .DistinctBy(survivor => survivor.Path, StringComparer.OrdinalIgnoreCase),
            ]),
            Notes = notes,
            Fallback = measured.Fallback,
            // Something held back while RetroArch runs, or behind a link, is something real left
            // unexamined, so a row with no steps must not read as clear.
            WasNotExamined = steps.Count == 0 && (held || reading.Declined.Count > 0),
            HasUnreadableRoot = reading.Unreadable,
        };
    }

    private bool IsRunning() => Inspector.FindRunning(RetroArchInstall.ProcessNames).Count > 0;

    private string HeldReason =>
        $"RetroArch is running, and writes its {Taken} while it is used, so they are left alone. Close "
        + "RetroArch, then scan again.";
}
