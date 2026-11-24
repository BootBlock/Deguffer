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
    private readonly PreferenceService _preferences;
    private readonly ScanTuner _tuner;
    private readonly IVolumeInventory _volumes;
    private readonly LatestSave _latest;

    /// <summary>The drives present and their kinds, as last found.</summary>
    private IReadOnlyList<DriveKind> _drives = [];

    /// <param name="tuner">The tuner the scans run with, so the drives listed are described as they will be scanned.</param>
    /// <param name="volumes">The inventory <paramref name="tuner"/> reads, dropped before each listing so a drive attached since is listed.</param>
    /// <param name="latest">The page's one notice of a failed save, shared with every form on the page.</param>
    public ScanSettingsViewModel(PreferenceService preferences, ScanTuner tuner, IVolumeInventory volumes, LatestSave latest)
    {
        _preferences = preferences;
        _tuner = tuner;
        _volumes = volumes;
        _latest = latest;

        Defaults = new ScanSettingsDefaults(preferences, this, () => Kind, Reset);
    }

    /// <summary>Each row's default, and the way back to it.</summary>
    public ScanSettingsDefaults Defaults { get; }

    /// <summary>
    /// Put one value back to its default, for the chosen kind alone where it is one kind's value. Every
    /// control reads the stored values again, as the row does not say which one its control shows.
    /// </summary>
    private void Reset(SettingSave row, SettingDefault setting) => Store(row, setting.Reset, string.Empty);

    /// <summary>
    /// Read every stored value again, after the page restored them all: every control, the folded
    /// header's summary, what each drive is scanned with, and each row's default.
    /// </summary>
    public void ShowStored()
    {
        OnPropertyChanged(string.Empty);
        DescribeDrives();
        Defaults.Follow();
    }

    public IReadOnlyList<string> KindNames { get; } = (string[])[.. ScanTuningText.Kinds.Select(ScanTuningText.MediaName)];

    /// <summary>The route picker's items, indexed as <see cref="RouteIndex"/> is.</summary>
    public IReadOnlyList<string> RouteNames => ScanTuningText.RouteNames;

    /// <summary>
    /// The section in one line, on the header that folds it away: the route, and how many values are
    /// set rather than left on Auto.
    /// </summary>
    public string Summary => ScanTuningText.Summary(Scanning);

    /// <summary>Index into the route combo box, ordered to match <see cref="ScanRoute"/>.</summary>
    public int RouteIndex
    {
        get => (int)Scanning.Route;
        set => Apply(RouteSave, current => current with { Route = (ScanRoute)value });
    }

    /// <summary>Which kind of drive the five boxes below set. Not stored: it is where the reader is looking.</summary>
    public int KindIndex
    {
        get;
        set
        {
            if (SetProperty(ref field, Math.Clamp(value, 0, ScanTuningText.Kinds.Count - 1)))
            {
                // Everything below the picker describes the kind it names, its defaults included.
                OnPropertyChanged(string.Empty);
                Defaults.Follow();
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

    public double MinimumTableListReadsInFlight => TableTuning.MinimumListReadsInFlight;

    public double MaximumTableListReadsInFlight => TableTuning.MaximumListReadsInFlight;

    public double MinimumTableParseThreads => TableTuning.MinimumParseThreads;

    public double MaximumTableParseThreads => TableTuning.MaximumParseThreads;

    /// <summary>
    /// How many threads parse the file table, for every kind of drive, on the terms
    /// <see cref="WalkThreads"/> states.
    /// </summary>
    public double TableParseThreads
    {
        get => Shown(Scanning.TableParseThreads);
        set => Apply(TableParseThreadsSave, current => current with { TableParseThreads = EnteredSetting.TableParseThreads(value) });
    }

    /// <summary>
    /// The chosen kind's thread count, or <see cref="double.NaN"/> on Auto, which a <c>NumberBox</c>
    /// shows as empty with its placeholder. Emptying the box is how a person goes back to Auto.
    /// </summary>
    public double WalkThreads
    {
        get => Shown(Chosen.WalkThreads);
        set => ApplyChosen(WalkThreadsSave, chosen => chosen with { WalkThreads = EnteredSetting.WalkThreads(value) });
    }

    /// <summary>The chosen kind's listing buffer in KiB, on the terms <see cref="WalkThreads"/> states.</summary>
    public double ListingBufferKiB
    {
        get => Shown(Chosen.ListingBufferKiB);
        set => ApplyChosen(ListingBufferSave, chosen => chosen with { ListingBufferKiB = EnteredSetting.ListingBufferKiB(value) });
    }

    /// <summary>The chosen kind's file-table read size in KiB, on the terms <see cref="WalkThreads"/> states.</summary>
    public double TableReadKiB
    {
        get => Shown(Chosen.TableReadKiB);
        set => ApplyChosen(TableReadSave, chosen => chosen with { TableReadKiB = EnteredSetting.TableReadKiB(value) });
    }

    /// <summary>The chosen kind's file-table reads in flight, on the terms <see cref="WalkThreads"/> states.</summary>
    public double TableReadsInFlight
    {
        get => Shown(Chosen.TableReadsInFlight);
        set => ApplyChosen(TableReadsInFlightSave, chosen => chosen with { TableReadsInFlight = EnteredSetting.TableReadsInFlight(value) });
    }

    /// <summary>
    /// The chosen kind's attribute-list reads in flight, on the terms <see cref="WalkThreads"/> states.
    /// </summary>
    public double TableListReadsInFlight
    {
        get => Shown(Chosen.TableListReadsInFlight);
        set => ApplyChosen(TableListReadsInFlightSave, chosen => chosen with { TableListReadsInFlight = EnteredSetting.TableListReadsInFlight(value) });
    }

    /// <summary>What an empty box means for the chosen kind, as its placeholder: <c>Auto (16)</c>.</summary>
    public string AutoWalkThreads => AutoText(Auto.Walk.Threads);

    public string AutoListingBufferKiB => AutoText(Auto.Walk.ListingBufferBytes / 1024);

    public string AutoTableReadKiB => AutoText(Auto.Table.ReadBytes / 1024);

    public string AutoTableReadsInFlight => AutoText(Auto.Table.ReadsInFlight);

    public string AutoTableListReadsInFlight => AutoText(Auto.Table.ListReadsInFlight);

    public string AutoTableParseThreads => AutoText(Auto.Table.ParseThreads);

    /// <summary>
    /// The count an empty box stands for, which its placeholder names, so the unit after the box
    /// agrees with it: "Auto (1) thread".
    /// </summary>
    public int AutoWalkThreadsCount => Auto.Walk.Threads;

    public int AutoTableReadsInFlightCount => Auto.Table.ReadsInFlight;

    public int AutoTableListReadsInFlightCount => Auto.Table.ListReadsInFlight;

    public int AutoTableParseThreadsCount => Auto.Table.ParseThreads;

    /// <summary>
    /// One line per drive present, saying what its kind is and what each scan of it runs with. Brought
    /// up to date in place, so a line the reader is on keeps its place.
    /// </summary>
    public ObservableCollection<DriveLine> Drives { get; } = [];

    /// <summary>
    /// Whether the last change made through each row could not be saved, for that row to say so, as
    /// the rest of the page does, through the page's one <see cref="LatestSave"/>.
    /// </summary>
    public SettingSave RouteSave { get; } = new();

    public SettingSave TableParseThreadsSave { get; } = new();

    public SettingSave WalkThreadsSave { get; } = new();

    public SettingSave ListingBufferSave { get; } = new();

    public SettingSave TableReadSave { get; } = new();

    public SettingSave TableReadsInFlightSave { get; } = new();

    public SettingSave TableListReadsInFlightSave { get; } = new();

    /// <summary>
    /// Find the drives present and their kinds, then describe them. Off the UI thread, because the
    /// first question about a drive opens its device. The kinds are kept, so a change to a value
    /// redescribes the drives without asking a device again.
    ///
    /// <para>The volumes are read again through <see cref="IVolumeInventory.RefreshAsync"/> rather than
    /// dropped: the list is shared, and a dropped one would be read again by whichever page on the UI
    /// thread asked for it next.</para>
    /// </summary>
    public async Task RefreshDrivesAsync(CancellationToken ct)
    {
        await _volumes.RefreshAsync(ct);
        _tuner.Invalidate();

        _drives = await Task.Run(() => _tuner.Drives(ct), ct);

        DescribeDrives();
    }

    private void DescribeDrives() =>
        LiveList.Show(
            Drives,
            (DriveLine[])[.. _drives.Select(drive => new DriveLine(drive.Drive, ScanTuningText.Describe(drive, Scanning)))],
            line => line.Drive);

    private ScanPreferences Scanning => _preferences.Current.Scanning;

    private StorageMedia Kind => ScanTuningText.Kinds[KindIndex];

    private MediaScanPreferences Chosen => Scanning.For(Kind);

    private VolumeTuning Auto => VolumeTuning.Resolve(ScanPreferences.Default, Kind);

    private static double Shown(int? chosen) => chosen ?? double.NaN;

    private static string AutoText(int value) => $"Auto ({value:N0})";

    private void ApplyChosen(
        SettingSave row,
        Func<MediaScanPreferences, MediaScanPreferences> change,
        [CallerMemberName] string setting = "")
    {
        var kind = Kind;
        Apply(row, current => current.With(kind, change(current.For(kind))), setting);
    }

    /// <param name="row">The row whose control made the change, which says so if it was not saved.</param>
    /// <param name="setting">The property whose control made the change, which is the setter calling this.</param>
    private void Apply(SettingSave row, Func<ScanPreferences, ScanPreferences> change, [CallerMemberName] string setting = "") =>
        Store(row, current => current with { Scanning = change(current.Scanning) }, setting);

    /// <param name="setting">The property whose control made the change, or empty where every control is to read back.</param>
    private void Store(SettingSave row, Func<AppPreferences, AppPreferences> change, string setting)
    {
        var outcome = _latest.Report(row, _preferences.Update(change));

        // As the rest of the page does: a rejected write puts every control back to what holds, and
        // an accepted one reads back what was stored, which the clamp can make differ from what was
        // typed.
        OnPropertyChanged(outcome is SaveOutcome.Refused ? string.Empty : setting);

        if (outcome is SaveOutcome.Saved)
        {
            OnPropertyChanged(nameof(Summary));
            DescribeDrives();
        }

        Defaults.Follow();
    }
}
