using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The tool archives Espressif's ESP-IDF installers download for every toolchain, debugger and utility
/// they install, and keep in <c>dist</c> after unpacking each into <c>tools</c>.
///
/// <para><b>Researched from source, not measured.</b> No machine this was written against has ESP-IDF
/// installed. Where the archives are, and what is never touched beside them, is
/// <see cref="EspIdfLayout"/> and <see cref="EspIdfArchives"/>.</para>
///
/// <para><b>Tier 1, by path.</b> Espressif's guide calls <c>dist</c> the folder "where the archives of the
/// tools are downloaded", and an archive is read only while its tool is installed. Each ESP-IDF version
/// pins its own builds of GCC, OpenOCD and QEMU, so the archives multiply with every version
/// installed.</para>
///
/// <para><b>§5.1 has no command that removes only the archives.</b>
/// <c>idf_tools.py uninstall --remove-archives</c> first removes every installed tool version the
/// active ESP-IDF does not use, and <c>tools</c> is shared by every ESP-IDF version on the machine, so it
/// breaks the others. That is uninstalling (§2). Its <c>--dry-run</c> lists unused tools only, never
/// archives. The Installation Manager's <c>--cleanup</c> runs only during an install, and its
/// <c>remove</c> and <c>purge</c> uninstall.</para>
///
/// <para><b>Nothing while the Installation Manager runs (§5.3).</b> It downloads each archive and then
/// unpacks it, one tool after another, and it writes with the sharing Rust's standard library gives, which
/// lets Windows delete a file it is still writing. So while it runs the archives are left alone, and the clean asks
/// again before it removes each one. <c>idf_tools.py</c> downloads and unpacks through Python's own
/// file objects, which Windows will not let another program delete while they are open, so an archive
/// it is writing or unpacking is refused to the removal by Windows itself.</para>
///
/// <para><b>The Installation Manager runs under the name it was downloaded or installed as.</b> The MSI
/// installs <c>eim.exe</c> and Scoop installs the GUI as <c>eim-gui.exe</c>, but the release assets are
/// <c>eim-cli-windows-x64.exe</c> and <c>eim-gui-windows-x64.exe</c>, and versioned copies such as
/// <c>eim-gui-windows-x64-v0.20.1.exe</c> (<c>_v0.17.4</c> up to 0.17.4). Espressif's documentation runs
/// the CLI by that name, the GUI download is the Installation Manager itself rather than an installer, and
/// WinGet's portable install keeps the asset's name behind its <c>eim</c> link. So the names are matched
/// by their beginnings. Not <c>eim</c> alone, which other programs' names start with, and not
/// <c>offline_installer_builder</c>, which never writes into <c>dist</c>.</para>
/// </summary>
public sealed class EspIdfArchiveProvider : CleanupProviderBase
{
    private static readonly ProcessNameRule InstallationManager =
        ProcessNameRule.Named("eim", "eim-gui").OrStartingWith("eim-cli-windows-", "eim-gui-windows-");

    private const string HeldReason =
        "The ESP-IDF Installation Manager is running, and it may be downloading or unpacking an archive here, "
        + "so the archives are left alone. Close the ESP-IDF Installation Manager, then scan again.";

    private const string DownloadsExploreReason =
        "This is the folder ESP-IDF downloads its tool archives into. Only the archives inside it may be removed.";

    private const string ToolsExploreReason =
        "This is an ESP-IDF tools folder, with the tools every ESP-IDF version on this machine shares. Only "
        + "the archives in its dist folder may be removed.";

    private const string DeclinedExploreReason =
        "ESP-IDF's tools are kept here. Deguffer does not treat this folder as ESP-IDF's own, so only "
        + "the entries ESP-IDF writes in it are refused.";

    private readonly ISystemDirectories _system;
    private readonly RunningProcessCheck _stillClosed;
    private readonly PassMemo<EspIdfLayout> _layout = new();
    private readonly PassMemo<EspIdfArchives> _archives = new();

    public EspIdfArchiveProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null,
        IVolumeInventory? volumes = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner,
            volumes: volumes)
    {
        _system = system ?? SystemDirectories.Current;
        _stillClosed = new RunningProcessCheck(Inspector, InstallationManager);
    }

    public override string Id => "esp-idf-archives";

    public override string Name => "ESP-IDF downloaded tool archives";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override ProviderBasis Basis => ProviderBasis.Researched;

    public override string WhatHappensOnNextUse =>
        "Installing a tool version again, for this or another ESP-IDF version, downloads its archive again. "
        + "The installed tools, every ESP-IDF version, its Python environments and the installers' records "
        + "are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.PackageCaches,
        Application = "ESP-IDF, Espressif's framework for ESP32 chips, and its Installation Manager",
        Publisher = "Espressif Systems",
        Purpose = "ESP-IDF's installers download an archive for every compiler, debugger and utility they "
            + "install, unpack it into the tools folder, and keep the archive. Every ESP-IDF version adds its own.",
        Recommendation = "Deguffer removes only the archives in each dist folder. ESP-IDF's own command for "
            + "this also uninstalls the tools other ESP-IDF versions use, so Deguffer does not run it.",
    };

    private EspIdfLayout Layout => _layout.Get(() => EspIdfLayout.Find(Environment, _system, Volumes));

    private EspIdfArchives Examine(CancellationToken ct) => _archives.Get(() => EspIdfArchives.Of(Layout.Folders, ct));

    /// <summary>Every answer here is configuration or the disk, which can change between one scan and the next.</summary>
    public override void InvalidateCaches()
    {
        _layout.Forget();
        _archives.Forget();
        base.InvalidateCaches();
    }

    /// <summary>
    /// Present where a <c>dist</c> folder is there, or may be, or where the Installation Manager's record
    /// is, or a folder a setting names was withheld.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default)
    {
        var layout = Layout;

        return Task.FromResult(layout.Folders.Count > 0 || layout.HasInstallationManager || layout.LeftSomethingUnexamined);
    }

    /// <summary>
    /// §5.2 for Explore. Each tools folder recognises only the way down to its <c>dist</c> and to any tools
    /// folder inside it, and each <c>dist</c> only the
    /// archive files in it, or nothing while the Installation Manager runs. A declined folder refuses the
    /// entries ESP-IDF writes in it.
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default)
    {
        var layout = Layout;
        var running = InstallerIsRunning();

        return Task.FromResult<IReadOnlyList<ToolRoot>>(
        [
            // dist, and the way to each tools folder inside another, are recognised only to open the way
            // down to the archives. Each is a root itself, so Explore still refuses it, and with it the
            // files nobody classified.
            .. layout.Folders.SelectMany(folder => ToolRoot.WayDown(
                folder.Path,
                [folder.Downloads, .. layout.Folders.Select(other => other.Path)],
                ToolsExploreReason)),
            .. layout.Folders.Select(folder => running
                ? new ToolRoot(folder.Downloads, HeldReason, static _ => false)
                : new ToolRoot(folder.Downloads, DownloadsExploreReason, static child =>
                    child.Kind == ChildKind.File && EspIdfArchives.IsArchiveName(child.Name))),
            .. layout.DeclinedFolders.Select(folder => ToolRoot.Sparing(folder, DeclinedExploreReason, EspIdfLayout.WrittenEntries)),
        ]);
    }

    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>([.. Layout.Folders.Select(folder => CleanedPlace.Whole(folder.Downloads))]);

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var layout = Layout;

        if (layout.Folders.Count == 0 && layout.Notes.Count == 0)
        {
            return EmptyPlan("ESP-IDF has downloaded no tool archives in this account.");
        }

        var examined = Examine(ct);
        var notes = new List<PlanNote>(examined.Notes);
        notes.AddRange(layout.Notes);

        var held = InstallerIsRunning()
            ? examined.Reached.Where(reached => reached.Archives.Count > 0).Select(reached => reached.Downloads).ToList()
            : [];

        if (held.Count > 0)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Warning, $"Left ESP-IDF's tool archives alone: {HeldReason}"));
        }

        var (steps, measured) = await PlanDeletionsAsync(
            [.. examined.Archives.Where(_ => held.Count == 0).Select(archive => archive with { UseCheck = _stillClosed })],
            keep,
            ct).ConfigureAwait(false);

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        if (examined.Archives.Count == 0 && !examined.LeftSomethingOut && !layout.LeftSomethingUnexamined)
        {
            notes.Insert(0, new PlanNote(PlanNoteSeverity.Information, "ESP-IDF keeps no downloaded tool archives."));
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            // Held first, so a held dist is named for why it was held rather than as the folder only the
            // contents of are removed.
            ProtectedPaths = await ProtectAsync(
                [
                    .. held.Select(path => (Path: path, Reason: HeldReason)),
                    .. examined.Scan.Protected,
                    .. examined.Kept,
                ],
                ct).ConfigureAwait(false),
            Notes = notes,
            Fallback = measured.Fallback,
            // Archives held back while the Installation Manager runs, or anything behind a link, unread or
            // withheld, is something real left unexamined, so a row with no steps must not read as clear.
            WasNotExamined = steps.Count == 0 && (held.Count > 0 || examined.LeftSomethingOut || layout.LeftSomethingUnexamined),
            HasUnreadableRoot = examined.Unreadable,
        };
    }

    private bool InstallerIsRunning() => Inspector.FindRunning(InstallationManager).Count > 0;
}
