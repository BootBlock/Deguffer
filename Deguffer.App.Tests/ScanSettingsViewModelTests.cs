using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Scanning section holds the stored scan settings and the controls together: each box sets
/// the kind of drive the picker names, an emptied box is Auto, a failed write says so, and the drives
/// present are described as their scans will run. What Auto means and what a typed number becomes
/// are Core's, and proven in <c>ScanTuningTests</c>.
/// </summary>
public sealed class ScanSettingsViewModelTests : IDisposable
{
    private const byte Nvme = 0x11;
    private const byte Sata = 0x0B;

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public ScanSettingsViewModelTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private (ScanSettingsViewModel Section, PreferenceService Preferences, FakeStorageQueries Queries) Section()
    {
        var preferences = new PreferenceService(new PreferenceStore(_environment));
        var queries = new FakeStorageQueries()
            .Volume(@"C:\", 1).Disk(1, Nvme, seekPenalty: false)
            .Volume(@"D:\", 0).Disk(0, Sata, seekPenalty: true);
        var volumes = new FakeVolumeInventory().With(@"C:\").With(@"D:\");
        var tuner = new ScanTuner(preferences, new VolumeMediaCache(queries), volumes);

        return (new ScanSettingsViewModel(preferences, tuner, volumes), preferences, queries);
    }

    [Fact]
    public void EachBoxSetsTheKindOfDriveThePickerNames()
    {
        var (section, preferences, _) = Section();

        section.KindIndex = KindIndex(section, StorageMedia.Rotational);
        section.WalkThreads = 2;
        section.ListingBufferKiB = 64;
        section.TableReadKiB = 512;

        Assert.Equal(new MediaScanPreferences(2, 64, 512), preferences.Current.Scanning.Rotational);
        Assert.Equal(MediaScanPreferences.Auto, preferences.Current.Scanning.Nvme);
        Assert.False(section.SaveFailed);
    }

    /// <summary>Emptying a box is how a person goes back to Auto, and an Auto box reads back empty.</summary>
    [Fact]
    public void AnEmptiedBoxGoesBackToAuto()
    {
        var (section, preferences, _) = Section();

        section.WalkThreads = 8;
        section.WalkThreads = double.NaN;

        Assert.Null(preferences.Current.Scanning.For(Kind(section)).WalkThreads);
        Assert.True(double.IsNaN(section.WalkThreads));
    }

    /// <summary>A number past the bound is stored at the bound, and the box shows what was stored.</summary>
    [Fact]
    public void ABoxReadsBackWhatWasStoredRatherThanWhatWasTyped()
    {
        var (section, _, _) = Section();

        section.WalkThreads = 1000;

        Assert.Equal(WalkTuning.MaximumThreads, section.WalkThreads);
    }

    [Fact]
    public void ChoosingAnotherKindShowsThatKindsValuesAndItsAuto()
    {
        var (section, preferences, _) = Section();
        preferences.Update(current => current with
        {
            Scanning = current.Scanning.With(StorageMedia.SolidState, new MediaScanPreferences(WalkThreads: 6)),
        });

        section.KindIndex = KindIndex(section, StorageMedia.SolidState);

        Assert.Equal(6, section.WalkThreads);
        Assert.True(double.IsNaN(section.TableReadKiB));
        Assert.Equal(
            $"Auto ({VolumeTuning.Resolve(ScanPreferences.Default, StorageMedia.SolidState).Table.ReadBytes / 1024:N0})",
            section.AutoTableReadKiB);
    }

    [Fact]
    public void TheRouteIsStored()
    {
        var (section, preferences, _) = Section();

        section.RouteIndex = (int)ScanRoute.WalkOnly;

        Assert.Equal(ScanRoute.WalkOnly, preferences.Current.Scanning.Route);
    }

    [Fact]
    public void AWriteThatFailsSaysSoAndPutsEveryControlBack()
    {
        var (section, _, _) = Section();
        var raised = new List<string?>();
        section.PropertyChanged += (_, changed) => raised.Add(changed.PropertyName);

        // A folder where the file goes, so the write is refused as a locked profile refuses it.
        Directory.CreateDirectory(Path.Combine(_environment.LocalAppData, "Deguffer", "preferences.json"));

        section.WalkThreads = 4;

        Assert.True(section.SaveFailed);
        Assert.True(double.IsNaN(section.WalkThreads));
        Assert.Contains(string.Empty, raised);
    }

    /// <summary>
    /// The drives present are listed with their kinds and what each scan of them runs with, and a
    /// change to a value redescribes them without asking any device again.
    /// </summary>
    [Fact]
    public async Task TheDrivesPresentAreDescribedAsTheirScansWillRun()
    {
        var (section, _, queries) = Section();

        await section.RefreshDrivesAsync();

        Assert.Equal(["C:", "D:"], section.Drives.Select(line => line.Drive));
        Assert.StartsWith("C: (NVMe SSD): ", section.Drives[0].Text, StringComparison.Ordinal);
        Assert.StartsWith("D: (Spinning disk): ", section.Drives[1].Text, StringComparison.Ordinal);

        var asked = queries.ExtentsAsked;
        var nvmeLine = section.Drives[0];

        section.KindIndex = KindIndex(section, StorageMedia.Rotational);
        section.WalkThreads = 3;

        Assert.Contains("3 threads,", section.Drives[1].Text, StringComparison.Ordinal);
        Assert.Same(nvmeLine, section.Drives[0]);
        Assert.Equal(asked, queries.ExtentsAsked);
    }

    private static int KindIndex(ScanSettingsViewModel section, StorageMedia media) =>
        section.KindNames.ToList().IndexOf(ScanTuningText.MediaName(media));

    private static StorageMedia Kind(ScanSettingsViewModel section) =>
        Enum.GetValues<StorageMedia>().Single(media => ScanTuningText.MediaName(media) == section.KindNames[section.KindIndex]);
}
