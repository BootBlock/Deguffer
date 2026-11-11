using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The zips STM32CubeMX and STM32CubeIDE download each STM32 firmware package as, kept in the repository
/// beside the folder each was unpacked into.
///
/// <para><b>Tier 2, where the issue that proposed this said Tier 1.</b> A package is installed by its
/// folder, which projects use, and the zip is not read to use it. It is read again to install a patch:
/// STM32CubeIDE's log shows it unpacking a base package's zip it already held before it applied the
/// patch's, and ST's staff tell users installing a patch offline to put the base zip in the repository
/// first. So the next patch needs the zip back, which is expected to be a download (no source says so
/// outright), and an offline patch install fails until it is back. Each zip is an item, so the base of a package that is patched offline can be
/// kept.</para>
///
/// <para><b>Researched.</b> No installation has been seen. The sources are in
/// <c>docs/cache-locations.md</c>. Where they do not settle something, nothing is offered.</para>
///
/// <para><b>§5.2.</b> The repository is never a target. Only a zip named as the updater names a
/// package's download, whose package folder with its <c>package.xml</c> is beside it, and which holds
/// that folder and nothing else, is offered (<see cref="Stm32CubeFirmwareArchive"/>). Every package
/// folder, <c>Packs</c>, every other file and anything unrecognised is Tier 4, and §5.6 asserts each
/// survived.</para>
///
/// <para><b>§5.1 has nothing to prefer.</b> Neither tool has a command that removes a download and
/// keeps its package.</para>
///
/// <para><b>Nothing while ST's tools run (§5.3).</b> They download, unpack and patch in the repository,
/// and a patch reads the base zip while it is installed. So while either is in the process table the
/// zips are left alone and refused in Explore, and the clean asks again before each removal.</para>
/// </summary>
public sealed class Stm32CubeFirmwareZipProvider : CleanupProviderBase
{
    private const string HeldReason =
        "STM32CubeMX or STM32CubeIDE is running, and either can be installing a package or a patch from "
        + "these zips, so they are left alone. Close both, then scan again.";

    private const string RepositoryReason =
        "This is the repository STM32CubeMX and STM32CubeIDE keep firmware packages in. Your projects use "
        + "the packages in it, so only the zips of packages already unpacked beside them may be removed.";

    private const string PackageReason =
        "A firmware package STM32CubeMX or STM32CubeIDE unpacked. Your projects use it, so it is kept.";

    private const string OtherReason =
        "Not the download of a package unpacked beside it, so it is left alone.";

    private const string LinkReason = "A link rather than a file, so what it points at was never classified.";

    private readonly ISystemDirectories _system;
    private readonly RunningProcessCheck _stillClosed;
    private readonly PassMemo<Stm32CubeRepositories> _repositories = new();
    private readonly PassMemo<Examination> _examination = new();

    public Stm32CubeFirmwareZipProvider(
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
        _stillClosed = new RunningProcessCheck(Inspector, Stm32CubeRepositories.ProcessNames);
    }

    public override string Id => "stm32cube-firmware-zips";

    public override string Name => "STM32Cube firmware package downloads";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override StepGrain Grain => StepGrain.Items;

    public override ProviderBasis Basis => ProviderBasis.Researched;

    public override string WhatHappensOnNextUse =>
        "Your projects and the firmware packages they use are untouched. STM32CubeIDE was seen unpacking "
        + "a package's zip again to install a patch to it, so the next patch to one of these packages "
        + "needs its zip back, which is expected to mean downloading it again. Installing a patch offline fails until you put that zip back in the repository, so keep "
        + "the zip of any package you patch offline.";

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.DeveloperTools,
        Application = "STM32CubeMX and STM32CubeIDE",
        Publisher = "STMicroelectronics",
        Purpose = "ST's tools download each STM32 firmware package as a zip into their repository, unpack it "
            + "into a folder beside the zip, and keep both. One user reported that removing the zips cut a "
            + "repository of over 40 GB to about a third of that.",
        Recommendation = "Remove these if you install patches online, or not at all. Deguffer removes only a "
            + "zip whose package is already unpacked beside it, and never a package your projects use.",
    };

    private Stm32CubeRepositories Repositories =>
        _repositories.Get(() => Stm32CubeRepositories.Find(Environment, _system, Volumes));

    private Examination Examine(CancellationToken ct) => _examination.Get(() => Examination.Of(Repositories, ct));

    /// <summary>Every answer here is configuration or the disk, which can change between one scan and the next.</summary>
    public override void InvalidateCaches()
    {
        _repositories.Forget();
        _examination.Forget();
        Volumes.Invalidate();
        base.InvalidateCaches();
    }

    /// <summary>
    /// Present where a zip is offered, or where something stood in the way of finding out: a link, a
    /// folder Windows would not list, or a repository named and withheld. A repository holding only
    /// packages is no row.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default)
    {
        var examined = Examine(ct);

        return Task.FromResult(examined.Targets.Count > 0 || examined.LeftSomethingOut || Repositories.Notes.Count > 0);
    }

    /// <summary>
    /// §5.2 for Explore. Each repository recognises exactly the zips the plan offers in it, as files,
    /// and nothing while ST's tools run.
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default)
    {
        var examined = Examine(ct);
        var running = IsRunning();
        var offered = new TargetedEntries(examined.Targets);

        return Task.FromResult<IReadOnlyList<ToolRoot>>(
        [
            .. examined.Read.Select(repository => running
                ? new ToolRoot(repository, HeldReason, static _ => false)
                : new ToolRoot(repository, RepositoryReason, child => offered.Recognises(repository, child))),
        ]);
    }

    /// <summary>The zips themselves. Nothing else in a repository is ever removed.</summary>
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>([.. Examine(ct).Targets.Select(target => CleanedPlace.Whole(target.Path))]);

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var repositories = Repositories;
        var examined = Examine(ct);

        if (examined.Targets.Count == 0 && !examined.LeftSomethingOut && repositories.Notes.Count == 0)
        {
            return EmptyPlan("STM32CubeMX and STM32CubeIDE have kept no zip of a package already unpacked beside it.");
        }

        var notes = new List<PlanNote>(repositories.Notes);
        notes.AddRange(examined.Notes);

        var held = IsRunning() && examined.Targets.Count > 0;

        if (held)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Warning, $"Left the STM32Cube firmware zips alone: {HeldReason}"));
        }

        var (steps, measured) = await PlanDeletionsAsync(
            held ? [] : [.. examined.Targets.Select(target => target with { UseCheck = _stillClosed })],
            keep,
            ct).ConfigureAwait(false);

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
            // Held first, so a repository is named for why it was held rather than as the folder only
            // some of the contents of are removed.
            ProtectedPaths = await ProtectAsync(
                [
                    .. examined.Read.Select(repository => (repository, held ? HeldReason : RepositoryReason)),
                    .. examined.Survivors,
                    .. examined.Declined,
                    .. repositories.Declined,
                ],
                ct).ConfigureAwait(false),
            Notes = notes,
            Fallback = measured.Fallback,
            // Zips held back while ST's tools run, or a repository behind a link or withheld, is something
            // real left unexamined, so a row with no steps must not read as clear.
            WasNotExamined = steps.Count == 0 && (held || examined.LeftSomethingOut || repositories.Notes.Count > 0),
            HasUnreadableRoot = examined.Unreadable || repositories.Unreached.Count > 0,
        };
    }

    private bool IsRunning() => Inspector.FindRunning(Stm32CubeRepositories.ProcessNames).Count > 0;

    /// <summary>
    /// One planning pass's reading of every repository, read once so presence, the plan and Explore agree.
    /// </summary>
    /// <param name="Read">The repositories that were listed.</param>
    /// <param name="Targets">The zips offered, each an item named by its package.</param>
    /// <param name="Survivors">Everything else in each repository, which §5.6 asserts survived.</param>
    /// <param name="Declined">Links in a repository, which are not followed.</param>
    /// <param name="Notes">Why a zip named as a download was not offered, and what could not be read.</param>
    /// <param name="Unreadable">Whether Windows would not list a repository.</param>
    private sealed record Examination(
        IReadOnlyList<string> Read,
        IReadOnlyList<DeletionTarget> Targets,
        IReadOnlyList<(string Path, string Reason)> Survivors,
        IReadOnlyList<(string Path, string Reason)> Declined,
        IReadOnlyList<PlanNote> Notes,
        bool Unreadable)
    {
        public bool LeftSomethingOut => Declined.Count > 0 || Unreadable;

        public static Examination Of(Stm32CubeRepositories repositories, CancellationToken ct)
        {
            var read = new List<string>();
            var targets = new List<DeletionTarget>();
            var survivors = new List<(string Path, string Reason)>();
            var declined = new List<(string Path, string Reason)>();
            var notes = new List<PlanNote>();
            var unreadable = false;

            foreach (var repository in repositories.Folders)
            {
                ct.ThrowIfCancellationRequested();

                if (FolderEntries.Of(repository) is not { } entries)
                {
                    notes.Add(UnreadableRoot.Note(repository));
                    unreadable = true;
                    continue;
                }

                read.Add(repository);

                // Each unpacked package, by the name the updater gives its download. Two packages can map to
                // one name, as V1.1.15 and V1.11.5 do, and then the zip is the download of only one of them.
                var packages = entries
                    .Where(entry => entry is DirectoryInfo
                        && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint)
                        && Stm32CubeFirmwareArchive.IsPackageName(entry.Name)
                        && LongPath.ProbeFile(Path.Combine(entry.FullName, Stm32CubeFirmwareArchive.PackageDescription)) is PathPresence.Present)
                    .GroupBy(entry => Stm32CubeFirmwareArchive.ArchiveName(entry.Name), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => (IReadOnlyList<string>)[.. group.Select(entry => entry.Name)],
                        StringComparer.OrdinalIgnoreCase);
                var unpacked = packages.Values.SelectMany(names => names).ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();

                    var path = LongPath.Display(entry.FullName);

                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        declined.Add((path, LinkReason));
                        notes.Add(CacheLevelWalk.Note(path));
                    }
                    else if (entry is FileInfo && Stm32CubeFirmwareArchive.IsArchiveName(entry.Name))
                    {
                        var paired = Pair(entry, packages);

                        if (paired.Package is not { } package)
                        {
                            survivors.Add((path, paired.Why!));
                            notes.Add(new PlanNote(PlanNoteSeverity.Information, $"Leaving '{entry.Name}' alone: {paired.Why}"));
                        }
                        else
                        {
                            targets.Add(new DeletionTarget(
                                path,
                                $"The zip {package} was downloaded as. The package is unpacked in the folder beside it, "
                                + "and a patch to it needs the zip again, which is expected to mean downloading it.",
                                Kind: TargetKind.File,
                                Identity: new ItemIdentity(entry.Name.ToLowerInvariant(), package),
                                Group: repository));
                        }
                    }
                    else
                    {
                        survivors.Add((path, entry is DirectoryInfo && unpacked.Contains(entry.Name) ? PackageReason : OtherReason));
                    }
                }
            }

            return new Examination(read, targets, survivors, declined, notes, unreadable);
        }

        /// <summary>
        /// The package a zip named as a download is the download of, or why it is not offered. Where two
        /// packages map to its name, the zip's own contents say which one it is.
        /// </summary>
        private static (string? Package, string? Why) Pair(FileSystemInfo zip, IReadOnlyDictionary<string, IReadOnlyList<string>> packages)
        {
            if (!packages.TryGetValue(zip.Name, out var candidates))
            {
                return (null, "the package it unpacks to is not beside it, so STM32CubeMX may still need it to "
                    + "install that package.");
            }

            var unread = false;

            foreach (var package in candidates)
            {
                switch (Stm32CubeFirmwareArchive.Contents(zip.FullName, package))
                {
                    case ArchiveContents.OnlyThePackage:
                        return (package, null);
                    case ArchiveContents.Unread:
                        unread = true;
                        break;
                }
            }

            return unread
                ? (null, "Deguffer could not read what it holds, so nothing shows that it is the download of a "
                    + "package beside it.")
                : (null, $"it holds something other than {string.Join(" or ", candidates)}, so it is not the "
                    + "download of a package beside it.");
        }
    }
}
