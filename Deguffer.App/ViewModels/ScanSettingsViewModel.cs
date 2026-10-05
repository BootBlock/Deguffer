using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.App.Shell;
using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Core.Viewing;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The Settings page's Scanning section: the route, the one value for the machine, the values for
/// each kind of drive, and what Auto chose for each drive present.
///
/// <para>Every decision here is Core's. <see cref="EnteredSetting"/> turns what was typed into what
/// is stored, <see cref="VolumeTuning.Resolve"/> says what Auto means, and <see cref="ScanTuner"/>
/// says which drives are present and of what kind. This maps them to what the controls expose.</para>
/// </summary>
public sealed partial class ScanSettingsViewModel : ObservableObject
{
    /// <summary>The kinds in the order the picker lists them, the common ones first.</summary>
    private static readonly StorageMedia[] Kinds =
    [
        StorageMedia.Nvme,
        StorageMedia.SolidState,
        StorageMedia.Rotational,
        StorageMedia.Removable,
        StorageMedia.Network,
        StorageMedia.Virtual,
        StorageMedia.Unknown,
    ];

    private readonly PreferenceService _preferences;
    private readonly ScanTuner _tuner;
    private readonly IVolumeInventory _volumes;

    /// <summary>The drives present and their kinds, as last found.</summary>
    private IReadOnlyList<DriveKind> _drives = [];

    /// <param name="tuner">The tuner the scans run with, so the drives listed are described as they will be scanned.</param>
    /// <param name="volumes">The inventory <paramref name="tuner"/> reads, dropped before each listing so a drive attached since is listed.</param>
    public ScanSettingsViewModel(PreferenceService preferences, ScanTuner tuner, IVolumeInventory volumes)
    {
        _preferences = preferences;
        _tuner = tuner;
        _volumes = volumes;
    }

    public IReadOnlyList<string> KindNames { get; } = [.. Kinds.Select(ScanTuningText.MediaName)];

    /// <summary>Index into the route combo box, ordered to match <see cref="ScanRoute"/>.</summary>
    public int RouteIndex
    {
        get => (int)Scanning.Route;
        set => Apply(current => current with { Route = (ScanRoute)value });
    }

    /// <summary>Which kind of drive the four boxes below set. Not stored: it is where the reader is looking.</summary>
    public int KindIndex
    {
        get;
        set
        {
            if (SetProperty(ref field, Math.Clamp(value, 0, Kinds.Length - 1)))
            {
                // Everything below the picker describes the kind it names.
                OnPropertyChanged(string.Empty);
            }
        }
    }

    public double MinimumWalkThreads => WalkTuning.MinimumThreads;

    public double MaximumWalkThreads => WalkTuning.MaximumThreads;

    public double MinimumListingBufferKiB => WalkTuning.MinimumListingBuffer / 1024;

    public double MaximumListingBufferKiB => WalkTuning.MaximumListingBuffer / 1024;

    public double MinimumTableReadKiB => TableTuning.MinimumReadBytes / 1024;

    public double MaximumTableReadKiB => TableTuning.MaximumReadBytes / 1024;

    public double MinimumTableReadsInFlight => TableTuning.MinimumReadsInFlight;

    public double MaximumTableReadsInFlight => TableTuning.MaximumReadsInFlight;

    public double MinimumTableParseThreads => TableTuning.MinimumParseThreads;

    public double MaximumTableParseThreads => TableTuning.MaximumParseThreads;

    /// <summary>
    /// How many threads parse the file table, for every kind of drive, on the terms
    /// <see cref="WalkThreads"/> states.
    /// </summary>
    public double TableParseThreads
    {
        get => Shown(Scanning.TableParseThreads);
        set => Apply(current => current with { TableParseThreads = EnteredSetting.TableParseThreads(value) });
    }

    /// <summary>
    /// The chosen kind's thread count, or <see cref="double.NaN"/> on Auto, which a <c>NumberBox</c>
    /// shows as empty with its placeholder. Emptying the box is how a person goes back to Auto.
    /// </summary>
    public double WalkThreads
    {
        get => Shown(Chosen.WalkThreads);
        set => ApplyChosen(chosen => chosen with { WalkThreads = EnteredSetting.WalkThreads(value) });
    }

    /// <summary>The chosen kind's listing buffer in KiB, on the terms <see cref="WalkThreads"/> states.</summary>
    public double ListingBufferKiB
    {
        get => Shown(Chosen.ListingBufferKiB);
        set => ApplyChosen(chosen => chosen with { ListingBufferKiB = EnteredSetting.ListingBufferKiB(value) });
    }

    /// <summary>The chosen kind's file-table read size in KiB, on the terms <see cref="WalkThreads"/> states.</summary>
    public double TableReadKiB
    {
        get => Shown(Chosen.TableReadKiB);
        set => ApplyChosen(chosen => chosen with { TableReadKiB = EnteredSetting.TableReadKiB(value) });
    }

    /// <summary>The chosen kind's file-table reads in flight, on the terms <see cref="WalkThreads"/> states.</summary>
    public double TableReadsInFlight
    {
        get => Shown(Chosen.TableReadsInFlight);
        set => ApplyChosen(chosen => chosen with { TableReadsInFlight = EnteredSetting.TableReadsInFlight(value) });
    }

    /// <summary>What an empty box means for the chosen kind, as its placeholder: <c>Auto (16)</c>.</summary>
    public string AutoWalkThreads => AutoText(Auto.Walk.Threads);

    public string AutoListingBufferKiB => AutoText(Auto.Walk.ListingBufferBytes / 1024);

    public string AutoTableReadKiB => AutoText(Auto.Table.ReadBytes / 1024);

    public string AutoTableReadsInFlight => AutoText(Auto.Table.ReadsInFlight);

    public string AutoTableParseThreads => AutoText(Auto.Table.ParseThreads);

    /// <summary>
    /// One line per drive present, saying what its kind is and what each scan of it runs with. Brought
    /// up to date in place, so a line the reader is on keeps its place.
    /// </summary>
    public ObservableCollection<DriveLine> Drives { get; } = [];

    /// <summary>Shown only when a write failed, as the rest of the page does.</summary>
    [ObservableProperty]
    public partial bool SaveFailed { get; set; }

    /// <summary>
    /// Find the drives present and their kinds, then describe them. Off the UI thread, because the
    /// first question about a drive opens its device. The kinds are kept, so a change to a value
    /// redescribes the drives without asking a device again.
    /// </summary>
    public async Task RefreshDrivesAsync(CancellationToken ct)
    {
        _volumes.Invalidate();
        _tuner.Invalidate();

        _drives = await Task.Run(() => _tuner.Drives(ct), ct);

        DescribeDrives();
    }

    private void DescribeDrives() =>
        LiveList.Show(
            Drives,
            [.. _drives.Select(drive => new DriveLine(drive.Drive, ScanTuningText.Describe(drive, Scanning)))],
            line => line.Drive);

    private ScanPreferences Scanning => _preferences.Current.Scanning;

    private StorageMedia Kind => Kinds[KindIndex];

    private MediaScanPreferences Chosen => Scanning.For(Kind);

    private VolumeTuning Auto => VolumeTuning.Resolve(ScanPreferences.Default, Kind);

    private static double Shown(int? chosen) => chosen ?? double.NaN;

    private static string AutoText(int value) => $"Auto ({value:N0})";

    private void ApplyChosen(
        Func<MediaScanPreferences, MediaScanPreferences> change,
        [CallerMemberName] string setting = "")
    {
        var kind = Kind;
        Apply(current => current.With(kind, change(current.For(kind))), setting);
    }

    /// <param name="setting">The property whose control made the change, which is the setter calling this.</param>
    private void Apply(Func<ScanPreferences, ScanPreferences> change, [CallerMemberName] string setting = "")
    {
        SaveFailed = !_preferences.Update(current => current with { Scanning = change(current.Scanning) });

        // As the rest of the page does: a rejected write puts every control back to what holds, and
        // an accepted one reads back what was stored, which the clamp can make differ from what was
        // typed.
        OnPropertyChanged(SaveFailed ? string.Empty : setting);

        if (!SaveFailed)
        {
            DescribeDrives();
        }
    }
}
