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
    /// Read the volumes again, unless the last reading is younger than <see cref="FreshFor"/>.
    /// </summary>
    /// <returns>Whether the machine was read, so a caller knows the rows may have changed.</returns>
    public bool Refresh()
    {
        var now = time.GetUtcNow();

        if (_readAt is { } readAt && now - readAt < FreshFor)
        {
            return false;
        }

        _readAt = now;

        volumes.Invalidate();

        _arriving.Clear();

        // Only volumes that can actually be read. An optical drive with no disc and a card reader
        // with no card are both mounted and both answer no, and offering them is offering a scan that
        // cannot start.
        foreach (var volume in volumes.Volumes.Where(v => v.IsReady && v.Kind != DriveType.Network))
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

        return true;
    }

    /// <summary>
    /// The row for the volume mounted at <paramref name="rootPath"/>, or null where the picker does
    /// not offer it. Null in, null out, so a path with no root and an absent drive are one case.
    /// </summary>
    public DriveEntry? Find(string? rootPath) =>
        rootPath is null
            ? null
            : Entries.FirstOrDefault(entry => entry.RootPath.Equals(rootPath, StringComparison.OrdinalIgnoreCase));
}
