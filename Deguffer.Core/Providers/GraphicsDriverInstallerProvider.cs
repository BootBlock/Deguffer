using System.Text.RegularExpressions;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// What NVIDIA's and AMD's driver installers unpack or download and leave behind: the payload of a
/// driver that is already installed, several hundred megabytes a release.
///
/// <para><b>Not measured.</b> No machine with any of these folders was available when this was
/// written, so every rule here comes from the installers themselves and from what their vendors and
/// users report. <c>docs/cache-locations.md</c> lists the sources. Where they do not settle a name,
/// the name is not recognised, which costs an incomplete reclaim rather than a guess.</para>
///
/// <para><b>Tier 2, where the survey that proposed this said Tier 1.</b> §3's Tier 1 is content its
/// producer re-creates on demand. Nothing re-creates an installer payload: the installed driver runs
/// from the driver store and never reads these, and the only thing that needs one again is
/// reinstalling that release, which means downloading it again. That is Tier 2's consequence exactly,
/// and it keeps a location nobody has measured out of the default selection.</para>
///
/// <para><b>§5.1 has nothing to prefer.</b> None of the three vendors ships a command that removes
/// its downloaded packages. AMD's installer clears its older payloads from <c>C:\AMD</c> itself from
/// Adrenalin 24.1.1 on, to save space, so AMD's own installer treats them as disposable, and
/// NVIDIA's installers from 576.80 on unpack into a temporary folder and remove it. What they leave
/// from before then is what this reaches.</para>
///
/// <para><b>§5.2 is the whole of the safety argument, and it is stricter here than for a tool's
/// cache.</b> Two of the three folders sit at the top of the system drive, so the drive is never
/// listed and every folder on the way down is reached by name: see
/// <see cref="DriverInstallerRoot"/>. Only the payload folder itself is listed, and in each one the
/// siblings that matter are named and asserted to have survived:</para>
/// <list type="bullet">
/// <item><c>C:\AMD\Chipset_Software</c> is the install source of the chipset driver. Windows
/// Installer reads it to repair, upgrade or remove that driver, and a reinstall fails with error
/// 1308 once it has gone.</item>
/// <item><c>C:\AMD\WU-CCC2</c> is what a driver Windows Update installed is removed through.</item>
/// <item><c>C:\ProgramData\NVIDIA Corporation</c> holds the NVIDIA app's own update store beside the
/// old downloader. Somebody who cleared that store reported games crashing afterwards, so it is not
/// reached at all.</item>
/// </list>
///
/// <para><b>Presence is a payload with something in it, never a folder existing.</b> An installer
/// that tidies up after itself removes the files and can leave the folders that held them, and an
/// AMD machine keeps <c>C:\AMD</c> for its chipset driver alone. A row for either would tell the user
/// there is something to reclaim when there is not.</para>
///
/// <para><b>No administrator rights, measured for the parents and inferred for the folders
/// themselves.</b> The top of the system drive was measured granting Authenticated Users inherited
/// modify rights on every folder created below it, and another vendor's folder there was measured
/// carrying them. <c>%PROGRAMDATA%\NVIDIA Corporation</c> was measured granting Everyone full
/// control. None of the three payload folders was on the machine, so an installer that sets rights
/// of its own on one is not ruled out. If one does, the removal is refused and reported, which is the
/// direction <see cref="EpicLauncherContentCacheProvider"/> explains is the right one to be wrong
/// in.</para>
/// </summary>
public sealed partial class GraphicsDriverInstallerProvider : CleanupProviderBase
{
    private const string LinkReason =
        "A link rather than a directory, so what it points at was never classified.";

    private readonly IReadOnlyList<DriverInstallerRoot> _roots;

    public GraphicsDriverInstallerProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
    {
        _roots = Declare(system ?? SystemDirectories.Current);
    }

    public override string Id => "graphics-driver-installers";

    public override string Name => "Graphics driver installer files";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override StepGrain Grain => StepGrain.Items;

    public override string WhatHappensOnNextUse =>
        "Nothing changes for the driver you are running, which Windows keeps in its own store. To "
        + "reinstall one of these releases, or to repair an older AMD Software installation that "
        + "asks for its setup files, download the installer again from the vendor.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "the NVIDIA and AMD graphics driver installers, and GeForce Experience",
        Publisher = "NVIDIA and AMD",
        Purpose = "A driver installer unpacks the whole driver package before installing it, and "
            + "older releases left that copy behind, one folder per release. GeForce Experience "
            + "also kept the driver packages it downloaded. Each one is several hundred megabytes.",
        Recommendation = "The driver you are running does not use these. AMD's own installer "
            + "clears its older copies itself, and the only cost of removing them is downloading a "
            + "release again should you want to reinstall it.",
    };

    /// <summary>
    /// §5.3: AMD's installer unpacks into <c>AMD-Software-Installer</c> and removes it when it
    /// finishes, so while it runs that folder is the installation in progress.
    /// </summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => ["AMDSoftwareInstaller"];

    /// <inheritdoc />
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        .. _roots.Select(root => new ToolRoot(
            root.Path,
            $"This is the {root.Label} folder. Deguffer removes the driver packages it recognises "
            + "inside it and nothing else, because other software keeps what it needs beside them.",
            root.Recognises)),
    ];

    /// <summary>
    /// A recognised payload that may hold something. One listing per folder, and only of the folders
    /// the table names, so answering never reaches the top of the drive.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default)
    {
        foreach (var root in _roots)
        {
            ct.ThrowIfCancellationRequested();

            switch (LongPath.ProbeDirectory(root.Path))
            {
                case PathPresence.Refused:
                    return Task.FromResult(true);

                case PathPresence.Absent:
                    continue;
            }

            var scan = ChildDirectories.Under(root.Path);

            if (scan.Unreadable
                || scan.Directories.Any(child =>
                    root.Recognises(child.Name) && DirectoryContent.MayBePresent(child.FullName)))
            {
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var notes = new List<PlanNote>();
        var targets = new List<DeletionTarget>();
        var survivors = new List<(string Path, string Reason)>();
        var links = 0;
        var declined = 0;
        var unreadable = false;

        foreach (var root in _roots)
        {
            ct.ThrowIfCancellationRequested();

            switch (Reach(root, survivors, notes))
            {
                case Reached.Refused:
                    unreadable = true;
                    continue;

                case Reached.Link:
                    links++;
                    continue;

                case Reached.Absent:
                    continue;
            }

            var scan = ChildDirectories.Under(root.Path);

            // Found by name a moment ago, and a listing right is separate from a traverse right. A
            // refusal here would otherwise leave a plan with no steps and nothing said, which the
            // shell renders as "Already clear" about a folder nobody read.
            if (scan.Unreadable)
            {
                notes.Add(UnreadableRoot.Note(root.Path));
                unreadable = true;
                continue;
            }

            foreach (var link in scan.Links)
            {
                links++;
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Leaving '{root.Label}\\{link.Name}' alone: it is a link to somewhere else, and "
                    + "Deguffer does not delete through a link."));
                survivors.Add((LongPath.Display(link.FullName), LinkReason));
            }

            foreach (var child in scan.Directories)
            {
                ct.ThrowIfCancellationRequested();

                var classification = root.Classify(root.Path, child.Name);
                var path = LongPath.Display(child.FullName);

                if (!classification.Tier.IsOfferable())
                {
                    // Protected by name as well as left out: the declined and the targeted are
                    // siblings under one parent, which is exactly when an over-broad rule takes both.
                    declined++;
                    notes.Add(new PlanNote(
                        PlanNoteSeverity.Information,
                        $"Leaving '{root.Label}\\{child.Name}' alone: {classification.Reason}"));
                    survivors.Add((path, classification.Reason));
                    continue;
                }

                // An installer that tidied up removed the files and left the folders. Offering one
                // would be a row with nothing in it, so it is neither offered nor mentioned.
                if (!DirectoryContent.MayBePresent(path))
                {
                    continue;
                }

                targets.Add(new DeletionTarget(
                    path,
                    classification.Reason,
                    DirectoryAge.Of(path, ct),
                    Identity: new ItemIdentity($"{root.Label}\\{child.Name}", $"{root.Vendor} {child.Name}"),
                    Facets: [new ItemFacet("Vendor", root.Vendor)],
                    Group: root.Vendor));
            }
        }

        if (targets.Count == 0 && links == 0 && declined == 0 && !unreadable)
        {
            return EmptyPlan("No graphics driver installer has left its files in the places Deguffer knows about.");
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
            ProtectedPaths = Protect([.. Deduplicate(survivors)]),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = unreadable,
            WasNotExamined = targets.Count == 0 && links > 0,
        };
    }

    private enum Reached
    {
        Present,
        Absent,
        Refused,
        Link,
    }

    /// <summary>
    /// Walk from the base to the payload folder by name, protecting each folder passed through.
    ///
    /// The walk is the point, for the reason <see cref="DeclaredLocations"/> gives: a junctioned
    /// <c>C:\NVIDIA</c> would hand the listing below the far side's folders, and a check on the payload
    /// folder alone would never see it. Survivors are committed only once the folder is reached, so
    /// §5.6 names the folders something could actually be taken out of.
    /// </summary>
    private static Reached Reach(
        DriverInstallerRoot root,
        List<(string Path, string Reason)> survivors,
        List<PlanNote> notes)
    {
        var passed = new List<(string Path, string Reason)>
        {
            (root.Base, "The folder this sits in must survive. It is never listed, and only the "
                + "driver packages named inside it are removed."),
        };

        foreach (var path in (IEnumerable<string>)[.. root.Containers, root.Path])
        {
            switch (LongPath.ProbeDirectory(path))
            {
                case PathPresence.Refused:
                    notes.Add(UnreadableRoot.UnreachedNote(LongPath.Display(path)));
                    survivors.Add((path, UnreadableRoot.UnreachedReason));
                    return Reached.Refused;

                case PathPresence.Absent:
                    return Reached.Absent;
            }

            if (LongPath.IsReparsePoint(path))
            {
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Leaving '{LongPath.Display(path)}' alone: it is a link to somewhere else, and "
                    + "Deguffer does not look through a link."));
                survivors.Add((path, LinkReason));
                return Reached.Link;
            }

            passed.Add((
                path,
                $"The {Path.GetFileName(path)} folder itself must survive. Only the driver "
                + "packages recognised inside it are removed."));
        }

        survivors.AddRange(passed);
        survivors.AddRange(root.ProtectedNames.Select(p => (Path.Combine(root.Path, p.Name), p.Reason)));

        return Reached.Present;
    }

    /// <summary>One entry per path, keeping the first reason. Two roots share the drive they sit on.</summary>
    private static IEnumerable<(string Path, string Reason)> Deduplicate(
        IEnumerable<(string Path, string Reason)> paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return paths.Where(p => seen.Add(p.Path));
    }

    /// <summary>
    /// The three folders, keeping only those whose base is a full path. Windows answers an empty
    /// string for a folder it cannot locate, and a folder named below an empty base would be a path
    /// relative to Deguffer's own working directory, which is a directory nobody pointed at. §5.2's
    /// direction is to reach nothing there, never to guess.
    /// </summary>
    private static IReadOnlyList<DriverInstallerRoot> Declare(ISystemDirectories system) =>
    [
        .. Candidates(system).Where(root => Path.IsPathFullyQualified(root.Base)),
    ];

    private static IEnumerable<DriverInstallerRoot> Candidates(ISystemDirectories system) =>
    [
        new DriverInstallerRoot(
            @"NVIDIA\DisplayDriver",
            "NVIDIA",
            system.SystemDrive,
            Path.Combine("NVIDIA", "DisplayDriver"),
            ClassifyNvidiaRelease,
            []),

        new DriverInstallerRoot(
            @"NVIDIA Corporation\Downloader",
            "NVIDIA",
            system.ProgramData,
            Path.Combine("NVIDIA Corporation", "Downloader"),
            ClassifyNvidiaDownload,
            [
                ("config", "GeForce Experience's settings for its downloader. It is not a package."),
                ("status.json", "GeForce Experience's record of what it has downloaded. It is not a package."),
            ]),

        new DriverInstallerRoot(
            "AMD",
            "AMD",
            system.SystemDrive,
            "AMD",
            ClassifyAmdPayload,
            [.. AmdKeptChildren.Select(c => (c.Key, c.Value))]),
    ];

    /// <summary>
    /// NVIDIA's manual installer unpacks into <c>DisplayDriver\&lt;release&gt;</c>, and a release is
    /// always three digits, a point and two digits. Anything else in that folder is not something the
    /// installer wrote there.
    /// </summary>
    private static ChildClassification ClassifyNvidiaRelease(string root, string name) =>
        NvidiaRelease().IsMatch(name)
            ? new ChildClassification(
                name,
                SafetyTier.RegenerableWithCost,
                $"NVIDIA driver {name}, unpacked by its installer and left behind after it installed.")
            : Unrecognised(name, "not a driver release NVIDIA's installer unpacks here.");

    /// <summary>
    /// GeForce Experience kept each driver it downloaded in a folder named by a long hexadecimal
    /// identifier, and the newest in <c>latest</c>. Its settings and its record of what it downloaded
    /// sit beside them, and are named on the root rather than recognised here.
    /// </summary>
    private static ChildClassification ClassifyNvidiaDownload(string root, string name) =>
        name.Equals("latest", StringComparison.OrdinalIgnoreCase) || DownloadIdentifier().IsMatch(name)
            ? new ChildClassification(
                name,
                SafetyTier.RegenerableWithCost,
                "A driver package GeForce Experience downloaded. It downloads a package again when it "
                + "needs one.")
            : Unrecognised(name, "not a driver package GeForce Experience downloads here.");

    /// <summary>
    /// AMD's installer has unpacked into <c>AMD-Software-Installer</c> since 23.7.1. Before that each
    /// release had a folder of its own, under names that were never consistent, so those are
    /// recognised by holding the display driver package rather than by name. The chipset driver's
    /// folders are refused first, whatever they hold, because Windows Installer needs them.
    /// </summary>
    private static ChildClassification ClassifyAmdPayload(string root, string name)
    {
        if (AmdKeptChildren.TryGetValue(name, out var kept))
        {
            return new ChildClassification(name, SafetyTier.DoNotTouch, kept);
        }

        if (name.Equals("AMD-Software-Installer", StringComparison.OrdinalIgnoreCase))
        {
            return new ChildClassification(
                name,
                SafetyTier.RegenerableWithCost,
                "Where AMD's installer unpacks itself. It removes this when an installation finishes, "
                + "so one still here is from an installation that stopped part-way.");
        }

        // Asked in two states: a refusal is not evidence that this folder is a driver package. Walked
        // segment by segment as well, because a probe resolves through a link on the way down, and a
        // package found through one says nothing about what the folder itself holds.
        var folder = Path.Combine(root, name);
        var marker = Path.Combine(folder, "Packages", "Drivers", "Display");

        return DerivedPath.FirstObstacleBetween(folder, marker) is null
            && LongPath.ProbeDirectory(marker) is PathPresence.Present
            ? new ChildClassification(
                name,
                SafetyTier.RegenerableWithCost,
                "An older AMD Software release, unpacked by its installer and left behind. AMD's own "
                + "installer clears these itself from Adrenalin 24.1.1 on.")
            : Unrecognised(name, "not a driver package AMD's installer unpacks here.");
    }

    /// <summary>
    /// What AMD keeps in <c>C:\AMD</c> that is not a payload, and what each one is. Named on the root as
    /// well as refused, so §5.6 asserts each survived.
    /// </summary>
    private static readonly Dictionary<string, string> AmdKeptChildren = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Chipset_Software"] = "The chipset driver's install source. Windows Installer reads it to "
            + "repair, upgrade or remove that driver, and cannot once it has gone.",
        ["Chipset_Driver_Installer"] = "An older chipset driver's install source. Whether anything "
            + "still reads it is not established, so it stays.",
        ["Chipset_SoftwareLogs"] = "The chipset driver installer's logs.",
        ["WU-CCC2"] = "What a driver Windows Update installed is removed through.",
    };

    private static ChildClassification Unrecognised(string name, string why) =>
        new(name, SafetyTier.DoNotTouch, $"It is {why}");

    [GeneratedRegex(@"^\d{3}\.\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex NvidiaRelease();

    [GeneratedRegex("^[0-9a-f]{16,}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DownloadIdentifier();
}
