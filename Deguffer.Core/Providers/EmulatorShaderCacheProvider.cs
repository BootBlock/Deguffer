using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The shaders console emulators compile for this GPU and keep, which reach many gigabytes on a machine
/// with a real library: Cemu's, RPCS3's, Dolphin's, PCSX2's, DuckStation's, PPSSPP's and Vita3K's,
/// those of the Nintendo Switch emulators Ryujinx, Eden, Citron and yuzu, and those of the Nintendo 3DS
/// emulators Azahar, Lime3DS and Citra.
///
/// <para><b>One provider over a table of layouts, as <see cref="GpuShaderCacheProvider"/> is.</b> The
/// tier, the consequence and the reasoning are one fact for every emulator here, and what differs is
/// where each keeps its data and what is cache inside it. That knowledge is richer than a vendor
/// root's, so each emulator is an <see cref="EmulatorLayout"/> of its own rather than a row: a
/// strategy for finding the root, a file proving it, and recognised paths from it rather than names
/// found anywhere.</para>
///
/// <para><b>Tier 2.</b> Each emulator compiles its shaders again, and RPCS3 its PPU and SPU modules
/// and Ryujinx each game's translated code too, but it does so while a game loads or while it is played: minutes before a game starts, or
/// stutter until it has been played through, rather than a slower next use.</para>
///
/// <para><b>§5.1 has nothing to prefer.</b> No emulator here offers an eviction command Deguffer can
/// call. RPCS3's, Cemu's, Vita3K's and Azahar's are menu items inside the running program, and Cemu's
/// and Azahar's remove the transferable caches as well. So §5.2 carries the whole burden, and everything a layout does not
/// name is left alone at every level.</para>
///
/// <para><b>Found where each emulator puts its data, and where the user says it is.</b> The fixed
/// locations are looked at on every machine. A portable install, and every RPCS3 and Vita3K install, is beside
/// the program wherever it was unpacked, so it is reached only through a folder the user declares in
/// Settings, see <see cref="EmulatorFolderStore"/>.</para>
///
/// <para><b>Nothing of an emulator's while it runs (§5.3).</b> An emulator writes its shader cache
/// while a game is played, so while it is in the process table every folder it proved is held back
/// and refused in Explore, and the clean asks again before each removal. A folder two emulators
/// proved, as two forks of yuzu or of Citra can, is held while either runs.</para>
/// </summary>
public sealed class EmulatorShaderCacheProvider : CleanupProviderBase
{
    public static readonly IReadOnlyList<EmulatorLayout> Layouts =
    [
        new CemuLayout(),
        new Rpcs3Layout(),
        new DolphinLayout(),
        new Pcsx2Layout(),
        new DuckStationLayout(),
        new PpssppLayout(),
        new Vita3KLayout(),
        new RyujinxLayout(),
        YuzuFamilyLayout.Eden,
        YuzuFamilyLayout.Citron,
        YuzuFamilyLayout.Yuzu,
        CitraFamilyLayout.Azahar,
        CitraFamilyLayout.Lime3DS,
        CitraFamilyLayout.Citra,
    ];

    private readonly EmulatorFolderStore _folders;
    private readonly ISystemDirectories _system;
    private readonly IReadOnlyDictionary<EmulatorLayout, RunningProcessCheck> _stillClosed;
    private readonly PassMemo<EmulatorCacheExamination> _examination = new();

    public EmulatorShaderCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        EmulatorFolderStore? folders = null,
        ISystemDirectories? system = null,
        IVolumeInventory? volumes = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner,
            volumes: volumes)
    {
        _folders = folders ?? new EmulatorFolderStore(Environment);
        _system = system ?? SystemDirectories.Current;
        _stillClosed = Layouts.ToDictionary(layout => layout, layout => new RunningProcessCheck(Inspector, layout.ProcessNames));
    }

    public override string Id => "emulator-shader-cache";

    public override string Name => "Emulator shader caches";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "The emulator compiles its shaders again, so each game stutters until it has been played for a "
        + "while, or waits longer before it starts. RPCS3 also compiles each game's code again the next "
        + "time it boots, which can take minutes, and Ryujinx translates it again over the next few boots. "
        + "Your saves, memory cards, save states, firmware, keys, installed games and settings are "
        + "untouched, and so are Cemu's and the 3DS emulators' transferable caches and the Switch "
        + "emulators' guest shader caches.";

    public override ProviderBasis Basis => ProviderBasis.Researched;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.Games,
        Application = "Cemu, RPCS3, Dolphin, PCSX2, DuckStation, PPSSPP, Vita3K, Ryujinx, Eden, Citron, yuzu, "
            + "Azahar, Lime3DS and Citra, the console emulators",
        Publisher = "each emulator's open-source project",
        Purpose = "An emulator translates each game's shaders for your graphics card as the game draws, "
            + "and keeps the result so the next session does not stutter while it translates them again.",
        Recommendation = "Remove these for a game you have finished with, or after a driver or emulator "
            + "update has made them stale. Deguffer finds each emulator where it keeps its data by "
            + "default. For a portable install, and for every RPCS3 and Vita3K install, add its folder under "
            + "Emulator folders in Settings.",
    };

    public override void InvalidateCaches()
    {
        _examination.Forget();
        base.InvalidateCaches();
    }

    /// <summary>
    /// The one pass the plan and Explore's refusals both read, kept for a planning pass (G4). A pass
    /// that is cancelled throws before it is kept, so the next caller looks again.
    /// </summary>
    private EmulatorCacheExamination Examine(CancellationToken ct) =>
        _examination.Get(() => EmulatorCacheExamination.Of(Layouts, _folders.Load(), Environment, WhyNotOwned, HoldsRetroArch, Reach, ct));

    /// <summary>
    /// Whether the RetroArch rows answer for <paramref name="folder"/>. A refusal counts, because those
    /// rows say so themselves, and "no emulator here" is not something a refusal established.
    /// </summary>
    private static bool HoldsRetroArch(string folder) => RetroArchInstall.ProgramIn(folder) is not PathPresence.Absent;

    private string? WhyNotOwned(string folder) =>
        ConfiguredFolder.WhyNotOwned(folder, Environment, _system, Volumes, TempRoots.Resolve(Environment, _system, Volumes).AccountFolders);

    /// <summary>
    /// Present where a proven root holds a cache folder, or where the plan has something to say: a
    /// link left alone, something unread, or an emulator folder holding no emulator. A root whose
    /// emulator has never compiled a shader is not presence.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default)
    {
        var examination = Examine(ct);

        return Task.FromResult(
            examination.Roots.Any(root => root.CacheFolders.Count > 0)
            || examination.Declined.Count > 0
            || examination.Unreadable
            || examination.Notes.Count > 0);
    }

    /// <summary>
    /// §5.2 as §7.1 reads it, at every level from a root to the cache: each root recognises only the
    /// way to its cache folders, and each cache folder only what this pass recognised in it. While an
    /// emulator runs, no root it proved recognises anything, whichever layout read the root. Every other path the plan names as
    /// protected is refused outright.
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default)
    {
        var examination = Examine(ct);
        var roots = new List<ToolRoot>();

        foreach (var root in examination.Roots)
        {
            if (RunningOwner(root) is { } running)
            {
                roots.Add(new ToolRoot(root.Path, HeldReason(running), static _ => false));
                roots.AddRange(root.CacheFolders.Select(folder => new ToolRoot(folder.Path, HeldReason(running), static _ => false)));
                continue;
            }

            var reason = $"This is {root.Layout.Name}'s folder, which holds your saves, firmware and settings "
                + "beside its shader cache. Deguffer removes only the shader cache inside it.";

            // A cache folder can sit inside another, as Eden's game folders and Azahar's pipeline
            // folder do, and Explore asks each root on its own and refuses by the deepest that
            // refuses. So the outer folder's one root recognises both its caches and the way into the
            // inner folder, which still refuses itself, and the way down declares no second root there.
            var cacheFolders = root.CacheFolders.Select(folder => folder.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

            roots.AddRange(ToolRoot.WayDown(root.Path, cacheFolders, reason).Where(level => !cacheFolders.Contains(level.Path)));
            roots.AddRange(root.CacheFolders.Select(folder => CacheFolderRoot(root, folder, cacheFolders)));
        }

        var declared = roots.Select(root => root.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        roots.AddRange(examination.Survivors
            .Where(survivor => !declared.Contains(survivor.Path))
            .DistinctBy(survivor => survivor.Path, StringComparer.OrdinalIgnoreCase)
            .Select(survivor => new ToolRoot(survivor.Path, survivor.Reason, static _ => false)));

        return Task.FromResult<IReadOnlyList<ToolRoot>>(roots);
    }

    /// <summary>
    /// Each cache folder of each emulator found where it keeps its data by default or in an emulator
    /// folder the user added, as the same examination the plan reads names it, including one a setting
    /// moved. Whole, because each layout recognises its caches by a rule rather than a list of names; the
    /// emulator's own folder, with the saves, firmware and installed games, is never reached.
    /// </summary>
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>(
        [
            .. Examine(ct).Roots
                .SelectMany(root => root.CacheFolders)
                .Select(folder => CleanedPlace.Whole(folder.Path)),
        ]);

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var examination = Examine(ct);

        if (examination.FoundNothing)
        {
            return EmptyPlan("No emulator's data was found where each keeps it, or in an emulator folder.");
        }

        var notes = new List<PlanNote>(examination.Notes);
        var held = new List<(EmulatorRoot Root, EmulatorLayout Running)>();

        foreach (var root in examination.Roots)
        {
            if (RunningOwner(root) is { } running)
            {
                held.Add((root, running));
            }
        }

        foreach (var layout in held.Select(root => root.Running).Distinct())
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Warning, $"Left {layout.Name}'s shader caches alone: {HeldReason(layout)}"));
        }

        // One check for each set of owners, so a folder two emulators share is asked about both.
        var stillClosed = new Dictionary<IReadOnlyList<EmulatorLayout>, IUseCheck>(ReferenceEqualityComparer.Instance);

        var (steps, measured) = await PlanDeletionsAsync(
            [
                .. examination.Targets
                    .Where(target => !target.Owners.Any(IsRunning))
                    .Select(target => new DeletionTarget(
                        target.Path,
                        target.Reason,
                        Kind: target.Kind,
                        Group: target.Layout.Name,
                        UseCheck: StillClosed(target.Owners, stillClosed))),
            ],
            keep,
            ct).ConfigureAwait(false);

        if (steps.Count == 0 && held.Count == 0 && examination.Declined.Count == 0 && !examination.Unreadable)
        {
            return EmptyPlan("No emulator has compiled a shader cache that Deguffer recognises.") with { Notes = notes };
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
            // Held first, so a held root is named for why it was held rather than as the folder only
            // the cache inside is taken from.
            ProtectedPaths = Protect(
            [
                .. held.SelectMany(held => new[] { held.Root.Path }.Concat(held.Root.CacheFolders.Select(folder => folder.Path))
                        .Select(path => (Path: path, Reason: HeldReason(held.Running))))
                    .Concat(examination.Survivors),
            ]),
            Notes = notes,
            Fallback = measured.Fallback,
            // A cache held back while its emulator runs, or behind a link, is something real left
            // unexamined, so a row with no steps must not read as clear.
            WasNotExamined = steps.Count == 0 && (held.Count > 0 || examination.Declined.Count > 0),
            HasUnreadableRoot = examination.Unreadable,
        };
    }

    /// <summary>
    /// <paramref name="folder"/>'s root for Explore: what the plan recognised in it, and the first
    /// folder on the way to each cache folder of <paramref name="root"/> inside it.
    /// </summary>
    private static ToolRoot CacheFolderRoot(EmulatorRoot root, EmulatorCacheFolderReading folder, IReadOnlySet<string> cacheFolders)
    {
        var wayIn = cacheFolders
            .Where(inner => LongPath.Contains(folder.Path, inner) && !inner.Equals(folder.Path, StringComparison.OrdinalIgnoreCase))
            .Select(inner => LongPath.Relative(folder.Path, inner).Split(Path.DirectorySeparatorChar)[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new ToolRoot(
            folder.Path,
            $"This is {root.Layout.Name}'s cache folder. Deguffer removes only the shader caches in it.",
            child => folder.Recognises(child) || (child.Kind == ChildKind.Folder && wayIn.Contains(child.Name)));
    }

    private bool IsRunning(EmulatorLayout layout) => Inspector.FindRunning(layout.ProcessNames).Count > 0;

    /// <summary>The first emulator that proved <paramref name="root"/>'s folder and is running, if any.</summary>
    private EmulatorLayout? RunningOwner(EmulatorRoot root) => root.Owners.FirstOrDefault(IsRunning);

    private IUseCheck StillClosed(IReadOnlyList<EmulatorLayout> owners, Dictionary<IReadOnlyList<EmulatorLayout>, IUseCheck> checks)
    {
        if (owners is [var only])
        {
            return _stillClosed[only];
        }

        if (!checks.TryGetValue(owners, out var check))
        {
            checks[owners] = check = new RunningProcessCheck(Inspector, [.. owners.SelectMany(owner => owner.ProcessNames)]);
        }

        return check;
    }

    private static string HeldReason(EmulatorLayout layout) =>
        $"{layout.Name} is running and writes its shader cache while a game is played, so the cache is left "
        + $"alone. Close {layout.Name}, then scan again.";
}
