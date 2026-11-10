using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;
using Deguffer.Core.Safety;

namespace Deguffer.App.ViewModels;

/// <summary>
/// What the Explore page is pointed at: the drive box and the folder beside it, as one target the
/// status line states a refusal of wherever the page will not scan it.
///
/// <para>Separate from <see cref="ExploreViewModel"/>, as <see cref="ExploreGrowth"/> is, because none
/// of it reads the tree on screen or the node being looked at. The page reads <see cref="Target"/>
/// as a scan starts, and tells this when it may act (<see cref="Gate"/>). The decisions are Core's:
/// where each way of re-pointing the page leaves it (<see cref="ExploreTarget"/>) and when the page
/// may act (<see cref="ExploreGate"/>).</para>
///
/// <para>A little past G1's 250 lines, because the code is short and the reasons are long: a drive box
/// written back by its own picker, and a reading of the drives that lands while somebody is choosing
/// one, each need saying why the write is or is not a person's choice. Splitting the four ways of
/// re-pointing the page apart would split the one guard they share.</para>
/// </summary>
public sealed partial class ExploreTargeting : ObservableObject
{
    private readonly IVolumeInventory _volumes;
    private readonly DriveList _drives;

    /// <summary>The page's status line, where a refusal is stated and taken back.</summary>
    private readonly StatusLineViewModel _status;

    /// <summary>
    /// True while this is writing the target itself, so a write that arrives as a change of drive is
    /// not taken for a person choosing one.
    ///
    /// <para>Two writers are not people. <see cref="Retarget"/> writes the drive and the folder one
    /// after the other, and the drive's handler would otherwise drop the folder it is about to set.
    /// And a picker whose selected entry stops being in the list where it was writes null back
    /// through its two-way binding while <see cref="RefreshDrivesAsync"/> is writing the rows, before
    /// this has put the selection back. Taken at face value that is the user choosing a different
    /// drive, so it drops the folder scope — and the refresh can happen as the picker opens, when
    /// somebody is only looking at the list. Rows are written over in place, so only a reading that
    /// finds the chosen drive gone still does this.</para>
    /// </summary>
    private bool _retargeting;

    /// <param name="time">What decides when the drive picker's last reading has gone stale.</param>
    /// <param name="probe">What asks whether a typed location is a folder. See <see cref="Typed"/>.</param>
    /// <param name="status">See <see cref="_status"/>.</param>
    internal ExploreTargeting(
        IVolumeInventory volumes,
        TimeProvider time,
        IPathProbe probe,
        StatusLineViewModel status)
    {
        _volumes = volumes;

        // Mapped letters are offered, because Explore pictures a share by walking it. Removing
        // anything on one stays refused, by FinalPlaces.
        _drives = new DriveList(volumes, time, NetworkDrives.Offered);
        _status = status;
        Typed = new ExploreTypedFolder(probe, ScopeToFolder) { CanRetarget = CanRun() };

        // The drives are not read here: a constructor cannot wait for a reading taken off the UI
        // thread, and the page reads them as it is first shown (RefreshDrivesAsync).
    }

    /// <summary>
    /// Raised once the page may be pointed somewhere else, with where it was pointed before, so
    /// whatever turns on the target asks again: the Scan button, and the elevation offer, which
    /// describes what the next scan would cover.
    /// </summary>
    public event EventHandler<ExploreTarget>? Retargeted;

    /// <summary>The volumes offered in the picker, each with what it is called and how full it is.</summary>
    public ObservableCollection<DriveEntry> Drives => _drives.Entries;

    /// <summary>The box a location is typed into, a share most often, which points the page once it is checked.</summary>
    public ExploreTypedFolder Typed { get; }

    [ObservableProperty]
    public partial DriveEntry? SelectedDrive { get; set; }

    /// <summary>
    /// The folder a scan is scoped to, or null for a whole drive.
    ///
    /// <para>Set from the system picker, so the path is one the shell confirmed exists, the same
    /// reason <see cref="SettingsViewModel"/>'s source folders go through one; or typed, where
    /// <see cref="Typed"/> confirms it before the page points at it.</para>
    ///
    /// <para>Written only through <see cref="Retarget"/>, which is what keeps the drive box, the
    /// elevation offer and the status line answering for it.</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScopedToFolder))]
    public partial string? ScopeFolder { get; private set; }

    /// <summary>
    /// What the page is pointed at, as the two controls state it. See <see cref="ExploreTarget"/>
    /// for what the next scan covers and why it may be refused.
    ///
    /// <para>Not bound to anything. The screen states the two halves separately, in the drive box
    /// and the folder beside it, and this is what the scan is actually pointed at.</para>
    /// </summary>
    public ExploreTarget Target => new(SelectedDrive?.RootPath, ScopeFolder);

    public bool IsScopedToFolder => Target.IsScopedToFolder;

    /// <summary>
    /// When the page may act, from the work under way and the picture on the map. The page hands it
    /// over as either changes. See <see cref="ExploreGate"/>.
    /// </summary>
    public ExploreGate Gate
    {
        get;
        set
        {
            field = value;
            ScanWholeDriveCommand.NotifyCanExecuteChanged();
            Typed.CanRetarget = CanRun();
        }
    }

    /// <summary>
    /// Point the next scan at <paramref name="folder"/> and everything below it.
    ///
    /// <para>The picking itself belongs to the page: it is a WinUI dialog needing a window handle,
    /// and this stays testable by knowing only about the path that comes back — the arrangement
    /// <see cref="SettingsViewModel.AddSourceRoot"/> already uses for the same dialog.</para>
    ///
    /// <para>A choice that is not a folder on a disk leaves the target where it was and says so,
    /// by <see cref="PickedFolder.OnDisk"/>.</para>
    /// </summary>
    /// <param name="folder">What the picker returned, which may name nothing on a disk.</param>
    public void ScopeTo(string folder)
    {
        if (PickedFolder.OnDisk(folder) is not { } onDisk)
        {
            _status.Say(StatusLine.Refused(ExploreStatusText.NotOnDisk(Target.Refusal(_volumes))));

            return;
        }

        ScopeToFolder(onDisk);
    }

    /// <summary>
    /// Point the next scan at <paramref name="folder"/>, which is known to be a folder, with the
    /// drive box moved to the volume holding it where the box offers one.
    /// </summary>
    private void ScopeToFolder(string folder) =>
        Retarget(Target, Target.ScopedTo(folder, _drives.Holding(folder)?.RootPath));

    /// <summary>
    /// The page being shown: read the drives (<see cref="RefreshDrivesAsync"/>), then point it at
    /// <paramref name="resuming"/> where an elevated relaunch asked for a target.
    ///
    /// <para>In that order and in one place, because <see cref="PointAt"/> finds the drive among the
    /// rows, and the rows are filled only once the reading lands off the UI thread. Pointed before
    /// then, a relaunch to rescan a whole drive found no row, and scanned nothing.</para>
    /// </summary>
    /// <returns>Whether the page now points where <paramref name="resuming"/> asked, so the caller may scan.</returns>
    public async Task<bool> ShownAsync(ExploreRequest? resuming, CancellationToken ct)
    {
        await RefreshDrivesAsync(ct);

        return resuming is { } requested && PointAt(requested.Drive, requested.Folder);
    }

    /// <summary>
    /// Point the page where an earlier instance was pointed, so an elevated replacement resumes the
    /// scan the user asked for instead of starting from nothing. See <see cref="ExploreRequest"/>
    /// and <see cref="ExploreTarget.Pointing"/>.
    /// </summary>
    /// <returns>
    /// Whether the page is now pointed at what was asked for. False means nothing was restored, and
    /// the caller must not scan on the strength of it.
    /// </returns>
    public bool PointAt(string? drive, string? folder)
    {
        // Asked of the rows, so it means something only once the drives have been read: see
        // ShownAsync, which is how the page calls it.
        if (Target.Pointing(_drives.Find(drive)?.RootPath, folder) is not { } pointed)
        {
            return false;
        }

        Retarget(Target, pointed);

        return true;
    }

    /// <summary>Drop a folder scope, so the next scan covers the whole drive again.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private void ScanWholeDrive() => Retarget(Target, Target.WholeDrive());

    /// <summary>
    /// A drive chosen in the picker, which drops any folder scope: see
    /// <see cref="ExploreTarget.Choosing"/>.
    ///
    /// <para>Only where a person chose it. See <see cref="_retargeting"/> for the writes that come
    /// from this type itself.</para>
    /// </summary>
    partial void OnSelectedDriveChanged(DriveEntry? oldValue, DriveEntry? newValue)
    {
        if (_retargeting)
        {
            return;
        }

        var before = new ExploreTarget(oldValue?.RootPath, ScopeFolder);

        Retarget(before, before.Choosing(newValue?.RootPath));
    }

    /// <summary>
    /// Point the page at <paramref name="to"/>, and make what it says about the target answer for
    /// where it now points.
    ///
    /// <para>Both halves are written under <see cref="_retargeting"/>, because the drive's handler
    /// would otherwise read the half-written pair as a person choosing a drive, and drop the folder
    /// this is setting.</para>
    /// </summary>
    /// <param name="from">
    /// Where the page was pointed before the move. Passed rather than read, because a drive chosen
    /// in the picker has already been written by the time its handler runs.
    /// </param>
    private void Retarget(ExploreTarget from, ExploreTarget to)
    {
        _retargeting = true;

        try
        {
            SelectedDrive = _drives.Find(to.Drive);
            ScopeFolder = to.Folder;
        }
        finally
        {
            _retargeting = false;
        }

        ExplainRefusal();
        Retargeted?.Invoke(this, from);
    }

    /// <summary>
    /// Read the volumes again where the last reading has gone stale, so the picker offers what is
    /// mounted now and states how full it is now. See <see cref="DriveList"/> for how often that is.
    ///
    /// <para>Called as the page is shown and as the picker opens rather than once at startup. A
    /// mount point does not go stale, but the space figures beside it do: a build, a download or a
    /// removal through this very page moves them.</para>
    ///
    /// <para>The chosen volume keeps its row, because a row is written over rather than replaced.
    /// Only a volume that has gone takes the selection with it.</para>
    ///
    /// <para>The machine is read off the UI thread, and the rows are brought up to date on it once
    /// the reading lands. A choice the user makes while the reading runs is theirs, so only the
    /// writing of the rows is guarded.</para>
    /// </summary>
    public async Task RefreshDrivesAsync(CancellationToken ct)
    {
        if (await _drives.ReadAsync(ct) is not { } volumes)
        {
            return;
        }

        // Read before the rows change, because the picker can write null over the selection while
        // the list under it changes. See _retargeting.
        var standing = Target;

        _retargeting = true;

        try
        {
            _drives.Show(volumes);
        }
        finally
        {
            _retargeting = false;
        }

        // Where the reading leaves the page, by DriveList.Choose and ExploreTarget.AfterReading.
        // Retarget ends by explaining a refusal, which is what keeps the page from opening pointed
        // at a refused volume with the button dead and nothing said. It also raises Retargeted
        // whatever moved, because a reading can change whether the chosen volume is refused without
        // changing which row is chosen — the row is written over, not replaced — so the selection
        // raises nothing and the Scan button would otherwise go on answering for the last reading.
        // §7.1: a button that scans a volume the status line has just refused is the page stating
        // one thing and doing another.
        Retarget(standing, standing.AfterReading(_drives.Choose(standing.Drive)?.RootPath));
    }

    /// <summary>
    /// State why the page will not scan what it is pointed at, and take the sentence back once it
    /// is pointed somewhere it will. See <see cref="ExploreStatusText.Retargeted"/>.
    /// </summary>
    private void ExplainRefusal() =>
        _status.Say(ExploreStatusText.Retargeted(_status.Line, Target.Refusal(_volumes)));

    private bool CanRun() => !Gate.Busy;
}
