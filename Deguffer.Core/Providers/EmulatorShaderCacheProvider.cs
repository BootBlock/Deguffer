using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The shaders console emulators compile for this GPU and keep, which reach many gigabytes on a machine
/// with a real library: Cemu's, RPCS3's, Dolphin's and PCSX2's.
///
/// <para><b>One provider over a table of layouts, as <see cref="GpuShaderCacheProvider"/> is.</b> The
/// tier, the consequence and the reasoning are one fact for every emulator here, and what differs is
/// where each keeps its data and what is cache inside it. That knowledge is richer than a vendor
/// root's, so each emulator is an <see cref="EmulatorLayout"/> of its own rather than a row: a
/// strategy for finding the root, a file proving it, and recognised paths from it rather than names
/// found anywhere.</para>
///
/// <para><b>Tier 2.</b> Each emulator compiles its shaders again, and RPCS3 its PPU and SPU modules
/// too, but it does so while a game loads or while it is played: minutes before a game starts, or
/// stutter until it has been played through, rather than a slower next use.</para>
///
/// <para><b>§5.1 has nothing to prefer.</b> No emulator here offers an eviction command Deguffer can
/// call. RPCS3's and Cemu's are menu items inside the running program, and Cemu's removes the
/// transferable cache as well. So §5.2 carries the whole burden, and everything a layout does not
/// name is left alone at every level.</para>
///
/// <para><b>Found where each emulator puts its data, and where the user says it is.</b> The fixed
/// locations are looked at on every machine. A portable install, and every RPCS3 install, is beside
/// the program wherever it was unpacked, so it is reached only through a folder the user declares in
/// Settings, see <see cref="EmulatorFolderStore"/>.</para>
///
/// <para><b>Nothing of an emulator's while it runs (§5.3).</b> An emulator writes its shader cache
/// while a game is played, so while it is in the process table its caches are held back and refused
/// in Explore, and the clean asks again before each removal.</para>
/// </summary>
public sealed class EmulatorShaderCacheProvider : CleanupProviderBase
{
    public static readonly IReadOnlyList<EmulatorLayout> Layouts =
        [new CemuLayout(), new Rpcs3Layout(), new DolphinLayout(), new Pcsx2Layout()];

    private readonly EmulatorFolderStore _folders;
    private readonly ISystemDirectories _system;
    private readonly IReadOnlyDictionary<EmulatorLayout, RunningProcessCheck> _stillClosed;
    private EmulatorCacheExamination? _examination;

    public EmulatorShaderCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        EmulatorFolderStore? folders = null,
        ISystemDirectories? system = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
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
        + "time it boots, which can take minutes. Your saves, memory cards, save states, firmware, "
        + "installed games and settings are untouched, and so is Cemu's transferable cache.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Cemu, RPCS3, Dolphin and PCSX2, the console emulators",
        Publisher = "each emulator's open-source project",
        Purpose = "An emulator translates each game's shaders for your graphics card as the game draws, "
            + "and keeps the result so the next session does not stutter while it translates them again.",
        Recommendation = "Remove these for a game you have finished with, or after a driver or emulator "
            + "update has made them stale. Deguffer finds each emulator where it keeps its data by "
            + "default. For a portable install, and for every RPCS3 install, add its folder under "
            + "Emulator folders in Settings.",
    };

    public override void InvalidateCaches()
    {
        _examination = null;
        base.InvalidateCaches();
    }

    /// <summary>
    /// The one pass the plan and Explore's refusals both read, kept for a planning pass (G4). A pass
    /// that is cancelled throws before it is kept, so the next caller looks again.
    /// </summary>
    private EmulatorCacheExamination Examine(CancellationToken ct) =>
        _examination ??= EmulatorCacheExamination.Of(Layouts, _folders.Load(), Environment, WhyNotOwned, HoldsRetroArch, ct);

    /// <summary>
    /// Whether the RetroArch rows answer for <paramref name="folder"/>. A refusal counts, because those
    /// rows say so themselves, and "no emulator here" is not something a refusal established.
    /// </summary>
    private static bool HoldsRetroArch(string folder) => RetroArchInstall.ProgramIn(folder) is not PathPresence.Absent;

    private string? WhyNotOwned(string folder) =>
        ConfiguredFolder.WhyNotOwned(folder, Environment, _system, TempRoots.Resolve(Environment, _system).AccountFolders);

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
    /// emulator runs, none of its roots recognises anything. Every other path the plan names as
    /// protected is refused outright.
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default)
    {
        var examination = Examine(ct);
        var roots = new List<ToolRoot>();

        foreach (var root in examination.Roots)
        {
            if (IsRunning(root.Layout))
            {
                roots.Add(new ToolRoot(root.Path, HeldReason(root.Layout), static _ => false));
                roots.AddRange(root.CacheFolders.Select(folder => new ToolRoot(folder.Path, HeldReason(root.Layout), static _ => false)));
                continue;
            }

            var reason = $"This is {root.Layout.Name}'s folder, which holds your saves, firmware and settings "
                + "beside its shader cache. Deguffer removes only the shader cache inside it.";

            roots.AddRange(ToolRoot.WayDown(root.Path, root.CacheFolders.Select(folder => folder.Path), reason));
            roots.AddRange(root.CacheFolders.Select(folder => new ToolRoot(
                folder.Path,
                $"This is {root.Layout.Name}'s cache folder. Deguffer removes only the shader caches in it.",
                folder.Recognised.Contains)));
        }

        var declared = roots.Select(root => root.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        roots.AddRange(examination.Survivors
            .Where(survivor => !declared.Contains(survivor.Path))
            .DistinctBy(survivor => survivor.Path, StringComparer.OrdinalIgnoreCase)
            .Select(survivor => new ToolRoot(survivor.Path, survivor.Reason, static _ => false)));

        return Task.FromResult<IReadOnlyList<ToolRoot>>(roots);
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var examination = Examine(ct);

        if (examination.FoundNothing)
        {
            return EmptyPlan("No Cemu, RPCS3, Dolphin or PCSX2 data was found where each keeps it, or in an emulator folder.");
        }

        var notes = new List<PlanNote>(examination.Notes);
        var held = examination.Roots
            .Where(root => IsRunning(root.Layout))
            .ToList();

        foreach (var layout in held.Select(root => root.Layout).Distinct())
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Warning, $"Left {layout.Name}'s shader caches alone: {HeldReason(layout)}"));
        }

        var heldLayouts = held.Select(root => root.Layout).ToHashSet();

        var (steps, measured) = await PlanDeletionsAsync(
            [
                .. examination.Targets
                    .Where(target => !heldLayouts.Contains(target.Layout))
                    .Select(target => new DeletionTarget(
                        target.Path,
                        target.Reason,
                        Kind: target.Kind,
                        Group: target.Layout.Name,
                        UseCheck: _stillClosed[target.Layout])),
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
                .. held.SelectMany(root => new[] { root.Path }.Concat(root.CacheFolders.Select(folder => folder.Path))
                        .Select(path => (Path: path, Reason: HeldReason(root.Layout))))
                    .Concat(examination.Survivors)
                    .DistinctBy(survivor => survivor.Path, StringComparer.OrdinalIgnoreCase),
            ]),
            Notes = notes,
            Fallback = measured.Fallback,
            // A cache held back while its emulator runs, or behind a link, is something real left
            // unexamined, so a row with no steps must not read as clear.
            WasNotExamined = steps.Count == 0 && (held.Count > 0 || examination.Declined.Count > 0),
            HasUnreadableRoot = examination.Unreadable,
        };
    }

    private bool IsRunning(EmulatorLayout layout) => Inspector.FindRunning(layout.ProcessNames).Count > 0;

    private static string HeldReason(EmulatorLayout layout) =>
        $"{layout.Name} is running and writes its shader cache while a game is played, so the cache is left "
        + $"alone. Close {layout.Name}, then scan again.";
}
