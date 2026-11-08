using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Exploring.Knowledge;
using Deguffer.Core.ModManagers;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Vortex's and Mod Organizer 2's download folders, found from the managers' own defaults and records,
/// measured, and reported with the managers' routes and no step.
///
/// <para>Every folder is in the test's own tree, reached through <see cref="FakeUserEnvironment"/> and
/// <see cref="FakeSystemDirectories"/>. The runner is a <see cref="FakeProcessRunner"/> told about no
/// program, so it throws for any command, and every test that plans also proves nothing was run.</para>
/// </summary>
public sealed class ModManagerDownloadProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public ModManagerDownloadProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string VortexDownloads => Path.Combine(_environment.RoamingAppData, "Vortex", "downloads");

    private string Instances => Path.Combine(_environment.LocalAppData, "ModOrganizer");

    private ModManagerDownloadProvider Provider() =>
        new(_system, _environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, volumes: new FakeVolumeInventory());

    private static string Write(string path, int bytes = 1024, string? text = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (text is null)
        {
            File.WriteAllBytes(path, new byte[bytes]);
        }
        else
        {
            File.WriteAllText(path, text);
        }

        return path;
    }

    /// <summary>A global instance, with <paramref name="settings"/> as its <c>[Settings]</c> lines.</summary>
    private string Instance(string name, string settings = "")
    {
        var folder = Path.Combine(Instances, name);
        Write(Path.Combine(folder, ModOrganizerInstances.SettingsName), text: $"[General]\r\ngameName=Skyrim\r\n[Settings]\r\n{settings}\r\n");
        return folder;
    }

    /// <summary>An archive and the record Mod Organizer 2 writes beside it.</summary>
    private static string Archive(string folder, string name, int bytes, string? meta)
    {
        var archive = Write(Path.Combine(folder, name), bytes);

        if (meta is not null)
        {
            Write(archive + ModOrganizerArchives.MetaExtension, text: $"[General]\r\nmodID=1\r\n{meta}\r\n");
        }

        return archive;
    }

    /// <summary>A path as Qt writes it into an INI file.</summary>
    private static string QtEscaped(string path) => path.Replace(@"\", @"\\", StringComparison.Ordinal);

    private static string Messages(CleanupPlan plan) => string.Join("\n", plan.Notes.Select(note => note.Message));

    /// <summary>
    /// Vortex's downloads are reported with a figure for each game, largest first, and the plan offers
    /// nothing: no step, a report-only row at Tier 3, and the route and the gap stated. Executing the plan
    /// leaves every archive and the staging folder beside it (§5.6).
    /// </summary>
    [Fact]
    public async Task VortexsDownloadsAreReportedByGameAndNothingIsOffered()
    {
        var skyrim = Write(Path.Combine(VortexDownloads, "skyrimse", "SkyUI-12604-5-2.7z"), 8192);
        var fallout = Write(Path.Combine(VortexDownloads, "fallout4", "Armorsmith.zip"), 2048);
        var staged = Write(Path.Combine(_environment.RoamingAppData, "Vortex", "skyrimse", "mods", "SkyUI", "SkyUI_SE.esp"));

        var provider = Provider();
        var plan = await provider.PlanAsync();
        var text = Messages(plan);

        Assert.True(await provider.IsPresentAsync());
        Assert.Empty(plan.Steps);
        Assert.True(plan.ReportsOnly);
        Assert.Equal(SafetyTier.UserData, plan.Tier);
        Assert.Equal(FindingStatus.ReportOnly, new Finding(provider, true, plan).ToStatus(isElevated: false));
        Assert.Contains($"Vortex's downloads, '{VortexDownloads}', take", text, StringComparison.Ordinal);
        Assert.Contains($"by game: skyrimse {FreeSpace.Format(8192L)}, fallout4 {FreeSpace.Format(2048L)}.", text, StringComparison.Ordinal);
        Assert.Contains(ModManagerRoutes.VortexRoute, text, StringComparison.Ordinal);
        Assert.Contains(ModManagerRoutes.VortexMovedFolder, text, StringComparison.Ordinal);
        Assert.Contains(ModManagerRoutes.WhyNothingIsOffered, text, StringComparison.Ordinal);
        Assert.DoesNotContain(ModManagerRoutes.ModOrganizerRoute, text, StringComparison.Ordinal);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(skyrim));
        Assert.True(File.Exists(fallout));
        Assert.True(File.Exists(staged));
    }

    /// <summary>Vortex in its shared mode downloads under <c>%PROGRAMDATA%\vortex</c>, and is reported as such.</summary>
    [Fact]
    public async Task VortexsSharedDownloadsAreReported()
    {
        var shared = Path.Combine(_system.ProgramData, "vortex", "downloads");
        Write(Path.Combine(shared, "skyrimse", "SkyUI.7z"));

        var plan = await Provider().PlanAsync();

        Assert.Contains($"Vortex's shared downloads, '{shared}', take", Messages(plan), StringComparison.Ordinal);
    }

    /// <summary>
    /// A global instance's downloads, at their default, split as Mod Organizer 2's own list splits them:
    /// a hidden download is left out of the list whatever else it says, <c>uninstalled</c> wins over
    /// <c>installed</c>, a paused download is neither, and an archive with no record or one not finished is
    /// neither. A <c>.meta</c> record is not an archive, even beside a record of its own. Nothing survives
    /// less than it was (§5.6).
    /// </summary>
    [Fact]
    public async Task AnInstancesDownloadsAreSplitByWhatItsRecordsMark()
    {
        var downloads = Path.Combine(Instance("Skyrim"), "downloads");
        var installed = Archive(downloads, "SkyUI.7z", 300, "installed=true\r\nuninstalled=false");
        var uninstalled = Archive(downloads, "Old.zip", 5000, "installed=true\r\nuninstalled=true");
        Archive(downloads, "Paused.rar", 7000, "installed=true\r\npaused=true");
        Archive(downloads, "Unrecorded.7z", 11000, meta: null);
        Archive(downloads, "Partial.7z" + ModOrganizerArchives.UnfinishedExtension, 13000, "installed=true");
        Archive(downloads, "Hidden.7z", 17000, "installed=true\r\nremoved=true");
        Write(installed + ModOrganizerArchives.MetaExtension + ModOrganizerArchives.MetaExtension, text: "[General]\r\ninstalled=true\r\n");

        var provider = Provider();
        var plan = await provider.PlanAsync();
        var text = Messages(plan);

        Assert.Empty(plan.Steps);
        Assert.Contains($"The Mod Organizer 2 instance 'Skyrim': its downloads, '{downloads}', take", text, StringComparison.Ordinal);
        Assert.Contains(
            $"Of that, {FreeSpace.Format(300L)} is archives Mod Organizer 2 lists as installed, and "
            + $"{FreeSpace.Format(5000L)} archives it lists as uninstalled. {FreeSpace.Format(17000L)} is archives it "
            + "hides from its download list, which its deletions reach only while the list shows hidden downloads.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(ModManagerRoutes.ModOrganizerRoute, text, StringComparison.Ordinal);
        Assert.DoesNotContain(ModManagerRoutes.VortexRoute, text, StringComparison.Ordinal);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(installed));
        Assert.True(File.Exists(uninstalled + ModOrganizerArchives.MetaExtension));
    }

    /// <summary>
    /// The archives' records are read while the download folders are measured, neither before nor after:
    /// the measurement is held until the read has started, and the read until the measurement is asked
    /// for. The plan still finishes, with the records' figures. A plan that read the records only once the
    /// measurement ended would wait forever, which the timeout turns into a failure, and one that read
    /// them before measuring would leave the read waiting out its patience.
    /// </summary>
    [Fact]
    public async Task AnInstancesRecordsAreReadWhileItsDownloadsAreMeasured()
    {
        var downloads = Path.Combine(Instance("Skyrim"), "downloads");
        Archive(downloads, "SkyUI.7z", 300, "installed=true");
        Archive(downloads, "Old.zip", 5000, "uninstalled=true");

        var scanner = new HeldUntilReadAndMeasurementBothStart(new FakeDirectoryScanner());
        var provider = new ModManagerDownloadProvider(
            _system, _environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, scanner, new FakeVolumeInventory());

        // The read's crew is the plan's only one before the measurement walks, and it is made on the read's
        // own thread before the first record is read. See WorkerCrew.Made.
        WorkerCrew.Made.Value = _ => scanner.ReadStarting();

        CleanupPlan plan;

        try
        {
            plan = await provider.PlanAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            WorkerCrew.Made.Value = null;
            scanner.Release();
        }

        Assert.True(scanner.ReadMetMeasurement);
        Assert.Contains(
            $"Of that, {FreeSpace.Format(300L)} is archives Mod Organizer 2 lists as installed, and "
            + $"{FreeSpace.Format(5000L)} archives it lists as uninstalled.",
            Messages(plan),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A folder of many archives, whose records are read several at once and finish in no set order, gives
    /// the totals each record's own figure adds up to. An archive with no record, a record Windows will not
    /// let be read, a record that is a folder and a paused download are each counted in neither, as Mod
    /// Organizer 2 lists them.
    /// </summary>
    [Fact]
    public async Task ManyRecordsReadAtOnceGiveTheTotalsTheirArchivesAddUpTo()
    {
        var downloads = Path.Combine(Instance("Skyrim"), "downloads");
        string?[] marks = ["installed=true", "uninstalled=true", "installed=true\r\nremoved=true", "installed=true\r\npaused=true", null];
        long installed = 0, uninstalled = 0, hidden = 0;

        for (var i = 0; i < 250; i++)
        {
            var bytes = 100 + i;
            var mark = marks[i % marks.Length];
            Archive(downloads, $"Mod{i}.7z", bytes, mark);

            switch (i % marks.Length)
            {
                case 0: installed += bytes; break;
                case 1: uninstalled += bytes; break;
                case 2: hidden += bytes; break;
            }
        }

        var unreadable = Archive(downloads, "Unreadable.7z", 7000, "installed=true");
        using var denied = DeniedDirectory.WithUnreadableContent(unreadable + ModOrganizerArchives.MetaExtension);
        Archive(downloads, "Folder.7z", 11000, meta: null);
        Directory.CreateDirectory(Path.Combine(downloads, "Folder.7z" + ModOrganizerArchives.MetaExtension));

        var plan = await Provider().PlanAsync();

        Assert.Contains(
            $"Of that, {FreeSpace.Format(installed)} is archives Mod Organizer 2 lists as installed, and "
            + $"{FreeSpace.Format(uninstalled)} archives it lists as uninstalled. {FreeSpace.Format(hidden)} is archives it "
            + "hides from its download list",
            Messages(plan),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A folder in <c>%LOCALAPPDATA%\ModOrganizer</c> without <c>ModOrganizer.ini</c> is not an instance,
    /// as Mod Organizer 2's instance manager decides, and the download handler's record is not one either.
    /// </summary>
    [Fact]
    public async Task OnlyAFolderHoldingItsSettingsIsAnInstance()
    {
        Write(Path.Combine(Instances, "Leftover", "downloads", "Mod.7z"));
        Write(Path.Combine(Instances, ModOrganizerInstances.HandlerRecordName), text: "[handlers]\r\nsize=0\r\n");

        var provider = Provider();

        Assert.False(await provider.IsPresentAsync());
        Assert.Empty(ModOrganizerInstances.Read(_environment, _system, new FakeVolumeInventory()).Instances);
    }

    /// <summary>
    /// The download folder is where <c>download_directory</c> puts it, with <c>%BASE_DIR%</c> read as
    /// <c>base_directory</c>, both in Qt's escaped form, and either separator.
    /// </summary>
    [Fact]
    public void TheDownloadFolderIsWhereTheSettingsPutIt()
    {
        var games = Path.Combine(_temp.Path, "Modding", "Skyrim, SE");
        Instance("Skyrim", $"base_directory=\"{QtEscaped(games)}\"\r\ndownload_directory=%BASE_DIR%/archives/new");

        var instance = Assert.Single(ModOrganizerInstances.Read(_environment, _system, new FakeVolumeInventory()).Instances);

        Assert.Equal(Path.Combine(games, "archives", "new"), instance.Downloads);
        Assert.False(instance.Portable);
    }

    /// <summary>
    /// A portable instance is found through the download handler's record of its executable, and an
    /// executable beside no settings file is an installation with no portable instance.
    /// </summary>
    [Fact]
    public async Task APortableInstanceIsFoundThroughTheDownloadHandlersRecord()
    {
        var portable = Path.Combine(_temp.Path, "Games", "MO2");
        Write(Path.Combine(portable, ModOrganizerInstances.SettingsName), text: "[Settings]\r\n");
        Write(Path.Combine(portable, "ModOrganizer.exe"));
        var archive = Archive(Path.Combine(portable, "downloads"), "SkyUI.7z", 4096, "installed=true");
        var installed = Path.Combine(_temp.Path, "Programs", "MO2");
        Write(Path.Combine(installed, "ModOrganizer.exe"));
        Write(
            Path.Combine(Instances, ModOrganizerInstances.HandlerRecordName),
            text: "[General]\r\nnoregister=false\r\n[handlers]\r\nsize=2\r\n"
                + $"1\\executable={QtEscaped(Path.Combine(portable, "ModOrganizer.exe"))}\r\n1\\schema=nxm\r\n"
                + $"2\\executable={Path.Combine(installed, "ModOrganizer.exe").Replace('\\', '/')}\r\n");

        var instance = Assert.Single(ModOrganizerInstances.Read(_environment, _system, new FakeVolumeInventory()).Instances);
        var plan = await Provider().PlanAsync();

        Assert.True(instance.Portable);
        Assert.Equal(portable, instance.Folder);
        Assert.Contains(
            $"The portable Mod Organizer 2 instance in '{portable}': its downloads, '{Path.GetDirectoryName(archive)}', take",
            Messages(plan),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A download folder set to one of the account's own folders is named and declined: it is not
    /// measured as the instance's, and Explore does not refuse the user's own folder as Mod Organizer 2's.
    /// </summary>
    [Fact]
    public async Task ADownloadFolderInTheAccountsOwnFoldersIsDeclined()
    {
        var own = Path.Combine(_environment.UserProfile, "Downloads");
        var mine = Write(Path.Combine(own, "holiday.zip"));
        Instance("Skyrim", $"download_directory={QtEscaped(own)}");

        var provider = Provider();
        var plan = await provider.PlanAsync();
        var policy = await ExploreActionPolicy.ForAsync(_system, _environment, new FakeVolumeInventory(), [provider]);

        Assert.Contains(
            $"The Mod Organizer 2 instance 'Skyrim': its settings name '{own}' for its downloads, which Deguffer does not "
            + "measure as the instance's:",
            Messages(plan),
            StringComparison.Ordinal);
        Assert.DoesNotContain(await provider.DiscoverToolRootsAsync(), root => root.Path.Equals(own, StringComparison.OrdinalIgnoreCase));
        Assert.True(policy.MayRemove(mine).IsAllowed);
    }

    /// <summary>A relative download folder names no folder Deguffer can tell, so it is declined and said.</summary>
    [Fact]
    public async Task ARelativeDownloadFolderIsDeclined()
    {
        Instance("Skyrim", "base_directory=Modding\r\ndownload_directory=archives");

        var plan = await Provider().PlanAsync();

        Assert.Contains(
            $"its settings name a download folder, which Deguffer does not measure as the instance's: {ConfiguredFolder.NotAFullPath}",
            Messages(plan),
            StringComparison.Ordinal);
    }

    /// <summary>Two instances that share one download folder have it counted once.</summary>
    [Fact]
    public async Task ASharedDownloadFolderIsCountedOnce()
    {
        var shared = Path.Combine(_temp.Path, "Mods", "downloads");
        Write(Path.Combine(shared, "SkyUI.7z"), 4096);
        Instance("Skyrim", $"download_directory={QtEscaped(shared)}");
        Instance("Skyrim VR", $"download_directory={QtEscaped(shared)}");

        var text = Messages(await Provider().PlanAsync());

        Assert.Contains("in 1 folder.", text, StringComparison.Ordinal);
        Assert.Contains($"keeps its downloads in '{shared}' too, which is counted once.", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// An instance that downloads into one of Vortex's game folders has that folder counted once in the
    /// total, inside Vortex's.
    /// </summary>
    [Fact]
    public async Task ADownloadFolderInsideAnotherIsCountedOnceInTheTotal()
    {
        var skyrim = Path.Combine(VortexDownloads, "skyrimse");
        Write(Path.Combine(skyrim, "SkyUI.7z"), 4096);
        Instance("Skyrim", $"download_directory={QtEscaped(skyrim)}");

        var text = Messages(await Provider().PlanAsync());

        Assert.Contains($"Mod managers keep {FreeSpace.Format(4096L)} of downloaded mod archives in 1 folder.", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Explore refuses each download folder and everything in it, with the manager's route, and leaves what
    /// is beside it: an instance's own folder is not its downloads.
    /// </summary>
    [Fact]
    public async Task ExploreRefusesEachDownloadFolderAndLeavesWhatIsBesideIt()
    {
        var vortex = Write(Path.Combine(VortexDownloads, "skyrimse", "SkyUI.7z"));
        var instance = Instance("Skyrim");
        var archive = Archive(Path.Combine(instance, "downloads"), "SkyUI.7z", 1024, "installed=true");
        var beside = Write(Path.Combine(instance, "notes.txt"), text: "mine");

        var provider = Provider();
        var policy = await ExploreActionPolicy.ForAsync(_system, _environment, new FakeVolumeInventory(), [provider]);

        var refusedVortex = policy.MayRemove(vortex);
        Assert.False(refusedVortex.IsAllowed);
        Assert.Contains("Vortex's Downloads page", refusedVortex.Reason, StringComparison.Ordinal);
        Assert.False(policy.MayRemove(VortexDownloads).IsAllowed);

        var refusedArchive = policy.MayRemove(archive);
        Assert.False(refusedArchive.IsAllowed);
        Assert.Contains("Mod Organizer 2's Downloads list", refusedArchive.Reason, StringComparison.Ordinal);
        Assert.False(policy.MayRemove(Path.Combine(instance, "downloads")).IsAllowed);

        Assert.True(policy.MayRemove(beside).IsAllowed);
    }

    /// <summary>Hovering a download folder in Explore says whose archives they are and how to remove them.</summary>
    [Fact]
    public void ExploreSaysWhoseDownloadsTheyAreAndHowToRemoveThem()
    {
        Write(Path.Combine(VortexDownloads, "skyrimse", "SkyUI.7z"));
        var downloads = Path.Combine(Instance("Skyrim"), "downloads");
        Directory.CreateDirectory(downloads);

        var guide = ItemGuide.For(_system, _environment, new FakeVolumeInventory());
        var vortex = guide.Describe(VortexDownloads);
        var instance = guide.Describe(downloads);

        Assert.NotNull(vortex);
        Assert.Contains("one folder per game", vortex.Summary, StringComparison.Ordinal);
        Assert.Contains("with Delete", vortex.Removal, StringComparison.Ordinal);
        Assert.NotNull(instance);
        Assert.Contains("the Mod Organizer 2 instance 'Skyrim'", instance.Summary, StringComparison.Ordinal);
        Assert.Contains("Delete Installed Downloads...", instance.Removal, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', vortex.Removal + instance.Removal);
    }

    /// <summary>
    /// An instance's settings that Windows will not describe are said rather than read as no instance, and
    /// the row still shows.
    /// </summary>
    [Fact]
    public async Task AnInstanceWhoseSettingsAreRefusedIsSaidRatherThanLeftOut()
    {
        var settings = Path.Combine(Instance("Skyrim"), ModOrganizerInstances.SettingsName);
        using var denied = DeniedDirectory.WithUnreadableFile(settings);

        var provider = Provider();
        var plan = await provider.PlanAsync();

        Assert.True(await provider.IsPresentAsync());
        Assert.Contains(plan.Notes, note => note.Severity == PlanNoteSeverity.Warning
            && note.Message.Contains($"a Mod Organizer 2 instance's settings is at '{settings}'", StringComparison.Ordinal));
    }

    /// <summary>A Vortex download folder Windows will not describe is shown as not measured, never as empty or absent.</summary>
    [Fact]
    public async Task AVortexFolderWindowsWillNotDescribeIsNotReportedAsEmpty()
    {
        Write(Path.Combine(VortexDownloads, "skyrimse", "SkyUI.7z"));
        using var denied = DeniedDirectory.WithUnreadableAttributes(VortexDownloads);

        var provider = Provider();
        var plan = await provider.PlanAsync();

        Assert.True(await provider.IsPresentAsync());
        Assert.Contains(plan.Notes, note => note.Severity == PlanNoteSeverity.Warning
            && note.Message.Contains($"Windows would not say what is at '{VortexDownloads}'", StringComparison.Ordinal));
    }

    /// <summary>No manager's folder and no record means no row.</summary>
    [Fact]
    public async Task NoManagerMeansNoRow()
    {
        Assert.False(await Provider().IsPresentAsync());
    }

    /// <summary>The row cleans nothing, so it names no cleaned place, and it is shipped.</summary>
    [Fact]
    public async Task TheRowCleansNothingAndIsShipped()
    {
        using var shipped = new ShippedProviders();

        Assert.Empty(await Provider().CleanedPlacesAsync());
        Assert.Contains(shipped.Providers, provider => provider is ModManagerDownloadProvider);
    }

    /// <summary>
    /// A scanner that measures a list of folders only once the records' read has started, and a read that
    /// waits, before its first record, for a measurement to be asked for: so each gets under way only
    /// while the other is.
    /// </summary>
    private sealed class HeldUntilReadAndMeasurementBothStart(FakeDirectoryScanner inner) : IDirectoryScanner
    {
        public ScanTuner Tuning => inner.Tuning;

        /// <summary>How long the read waits for the measurement. A read made to wait this long ran first.</summary>
        private static readonly TimeSpan ReadPatience = TimeSpan.FromSeconds(10);

        private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _measurementAsked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readMetMeasurement;
        private int _crewsSeen;

        /// <summary>Whether the read, once started, saw a measurement asked for within its patience.</summary>
        public bool ReadMetMeasurement => Volatile.Read(ref _readMetMeasurement) == 1;

        /// <summary>
        /// On the read's own thread, before its first record: say it has started, then wait for the
        /// measurement. Only the first crew is the read's: the measurement's walk makes one too, and can
        /// only once this has let it go.
        /// </summary>
        public void ReadStarting()
        {
            if (Interlocked.Exchange(ref _crewsSeen, 1) == 1)
            {
                return;
            }

            _readStarted.TrySetResult();

            if (_measurementAsked.Task.Wait(ReadPatience))
            {
                Interlocked.Exchange(ref _readMetMeasurement, 1);
            }
        }

        /// <summary>Let both go, so a plan the test gave up on does not stay held.</summary>
        public void Release()
        {
            _readStarted.TrySetResult();
            _measurementAsked.TrySetResult();
        }

        public async ValueTask<IReadOnlyList<ScanResult>> MeasureManyAsync(
            IReadOnlyList<ScanRequest> asked,
            MinimumAge keep = default,
            IProgress<ScanSize>? progress = null,
            CancellationToken ct = default)
        {
            _measurementAsked.TrySetResult();
            await _readStarted.Task.WaitAsync(ct).ConfigureAwait(false);
            return await inner.MeasureManyAsync(asked, keep, progress, ct).ConfigureAwait(false);
        }

        public ValueTask<ScanResult> MeasureAsync(
            string path,
            MinimumAge keep = default,
            ScanBounds bounds = default,
            IProgress<ScanSize>? progress = null,
            CancellationToken ct = default) =>
            inner.MeasureAsync(path, keep, bounds, progress, ct);

        public ValueTask<AgedScan> MeasureAgedAsync(string path, MinimumAge age, IProgress<ScanSize>? progress = null, CancellationToken ct = default) =>
            inner.MeasureAgedAsync(path, age, progress, ct);

        public ValueTask<IReadOnlyList<ScanResult>> MeasureFromDiskAsync(IReadOnlyList<ScanRequest> asked, CancellationToken ct = default) =>
            inner.MeasureFromDiskAsync(asked, ct);

        public ValueTask<IReadOnlyList<string>?> TryFindDirectoriesAsync(
            DirectorySearch search,
            string root,
            CancellationToken ct = default) =>
            inner.TryFindDirectoriesAsync(search, root, ct);

        public void Invalidate() => inner.Invalidate();

        public PassHold Hold() => inner.Hold();
    }
}
