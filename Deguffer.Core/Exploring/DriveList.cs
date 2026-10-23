using System.Collections.ObjectModel;
using Deguffer.Core.Safety;
using Deguffer.Core.Viewing;

namespace Deguffer.Core.Exploring;

/// <summary>
/// The volumes the Explore drive picker offers, in drive-letter order, read from the machine no more
/// often than <see cref="FreshFor"/> allows.
///
/// <para>Read when the page is shown and when the picker opens, and each time only where the last
/// reading has gone stale. Asking the machine on every open was more than wasted work: each reading
/// moved the free space on the system drive by a few bytes, and so replaced the picker's selected
/// entry under it as it opened.</para>
///
/// <para>The rows are kept and written over rather than rebuilt (see <see cref="DriveEntry"/>). A
/// drive that is plugged in or taken away between two readings costs one insertion or one removal,
/// and a drive whose figures moved costs nothing the list itself is told about.</para>
/// </summary>
public sealed class DriveList(IVolumeInventory volumes, TimeProvider time)
{
    /// <summary>
    /// How long one reading stands. Long enough that opening the picker twice in a row reads the
    /// machine once, and short enough that the space figures beside a drive are never far behind a
    /// download or a clean.
    /// </summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(1);

    private readonly List<DriveChoice> _arriving = [];

    private DateTimeOffset? _readAt;

    /// <summary>The volumes on offer, one row each, ordered by where they are mounted.</summary>
    public ObservableCollection<DriveEntry> Entries { get; } = [];

    /// <summary>
    /// Read the volumes again off the calling thread, unless the last reading is younger than
    /// <see cref="FreshFor"/>. The rows are not touched, so a caller that has to guard against what
    /// a change of rows does to a bound picker guards <see cref="Show"/> alone, and the user's own
    /// choices while the machine is being read are taken as theirs.
    ///
    /// <para>Resumes where it was called, because the caller goes on to <see cref="Show"/>, which
    /// writes <see cref="Entries"/>, a list bound to a control.</para>
    ///
    /// <para>The reading is the inventory's <see cref="IVolumeInventory.RefreshAsync"/>, never a
    /// drop of the list and a read of it here: that read asks every volume the machine has mounted, and a
    /// drive picker refreshes on the UI thread as it opens (#181).</para>
    /// </summary>
    /// <returns>What was read, or null where the last reading, finished or still under way, stands.</returns>
    public async Task<IReadOnlyList<LocalVolume>?> ReadAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();

        if (_readAt is { } readAt && now - readAt < FreshFor)
        {
            return null;
        }

        // Before the await, so a picker opened while the page's own reading runs does not start a
        // second one.
        _readAt = now;

        try
        {
            return await volumes.RefreshAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Nothing was read, so the next caller reads.
            _readAt = null;

            throw;
        }
    }

    /// <summary>
    /// Bring the rows to <paramref name="read"/>, as <see cref="ReadAsync"/> answered it. On the UI
    /// thread, because <see cref="Entries"/> is bound.
    /// </summary>
    public void Show(IReadOnlyList<LocalVolume> read)
    {
        _arriving.Clear();

        // Not volumes with nothing in them. An optical drive with no disc and a card reader with no
        // card are both mounted and both answer no, and offering them is offering a scan that cannot
        // start. A volume Windows would not describe is listed and refused, because the user can see
        // it in File Explorer and leaving it out would not say why. See DriveChoice.UnreadableRefusal.
        foreach (var volume in read.Where(v => v.Readiness is not VolumeReadiness.NoMedia && v.Kind != DriveType.Network))
        {
            _arriving.Add(DriveChoice.From(volume));
        }

        // By drive letter, as File Explorer lists them. Windows enumerates volumes in the order they
        // were mounted, which puts a card reader plugged in last week ahead of the system drive. A
        // volume mounted at a folder sorts under the drive that folder is on.
        _arriving.Sort(static (one, other) =>
            StringComparer.OrdinalIgnoreCase.Compare(one.RootPath, other.RootPath));

        LiveList.Show(
            Entries,
            _arriving,
            static entry => entry.RootPath,
            static choice => choice.RootPath,
            static choice => new DriveEntry(choice),
            static (entry, choice) => entry.Update(choice));
    }

    /// <summary>
    /// The row for the volume mounted at <paramref name="rootPath"/>, or null where the picker does
    /// not offer it. Null in, null out, so a path with no root and an absent drive are one case.
    /// </summary>
    public DriveEntry? Find(string? rootPath) =>
        rootPath is null
            ? null
            : Entries.FirstOrDefault(entry => entry.RootPath.Equals(rootPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The row the picker should name once the list has been read: <paramref name="chosen"/> where it
    /// is still offered, and otherwise the first volume Explore will scan.
    ///
    /// <para>A refused volume is listed and is not defaulted onto. It is in the list because the user
    /// can see the drive and needs to be told why it is not scanned, and it is not the default
    /// because opening the page on a drive whose Scan button is dead reads as an app that failed to
    /// start. Where every volume is refused there is nothing better to point at, so the first is
    /// named and the page says why it will not scan it.</para>
    /// </summary>
    public DriveEntry? Choose(string? chosen) =>
        Find(chosen)
        ?? Entries.FirstOrDefault(static listed => !listed.Choice.IsRefused)
        ?? Entries.FirstOrDefault();

    /// <summary>
    /// The row for the volume holding <paramref name="folder"/>, or null where the picker does not
    /// offer one.
    ///
    /// <para>Asked of the inventory rather than of <c>Path.GetPathRoot</c>, which reduces a path to
    /// a drive letter: a folder on a volume mounted at <c>C:\Mount</c> would otherwise name the disk
    /// that folder sits on, and both are in the list.</para>
    /// </summary>
    public DriveEntry? Holding(string folder) => Find(HostVolume.For(volumes, folder)?.RootPath);
}
