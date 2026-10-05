using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Configuration;

/// <summary>
/// Which of §5.5's two routes a scan may take.
///
/// <para>Stored by name, as the other choices are.</para>
/// </summary>
public enum ScanRoute
{
    /// <summary>
    /// The file table wherever it can be read, and the walk where it cannot. What every scan did
    /// before the route could be chosen.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// The walk, even where the file table could be read. Slower on a large folder, and the answer
    /// for somebody who suspects what the table says.
    /// </summary>
    WalkOnly = 1,
}

/// <summary>
/// How a scan reads one kind of storage. Each value is a number the user chose, or null for Auto,
/// which <see cref="Scanning.ScanTuner"/> resolves for the kind of drive.
///
/// <para>Stored as the user typed it, in whole threads and KiB. Nothing validates
/// <c>preferences.json</c> on the way in, so a value here can be outside its bounds, and the reader
/// clamps it as <see cref="Scanning.VolumeTuning.Resolve"/> reads it.</para>
/// </summary>
/// <param name="WalkThreads">How many folders the walk lists at once.</param>
/// <param name="ListingBufferKiB">How many KiB of entries each listing asks Windows for.</param>
/// <param name="TableReadKiB">How many KiB of records each read of the file table asks for.</param>
public sealed record MediaScanPreferences(
    int? WalkThreads = null,
    int? ListingBufferKiB = null,
    int? TableReadKiB = null)
{
    public static readonly MediaScanPreferences Auto = new();
}

/// <summary>
/// How a scan reads the disk: the route, and a <see cref="MediaScanPreferences"/> for each kind of
/// storage, because one machine often has drives of several kinds and a value right for one is wrong
/// for another.
///
/// <para><b>These change how fast a scan is, never what it finds.</b> Neither route relaxes a rule
/// for any value here: the table is still read whole and a short read still ends it, the walk still
/// never follows a reparse point, and a folder Windows refused is still reported. So no value here
/// can change what a plan offers or what a clean removes.</para>
///
/// <para>A member per kind rather than a dictionary, so the record compares by value as the rest of
/// <see cref="AppPreferences"/> does, and a settings file written before a kind existed still reads
/// with that kind on Auto.</para>
/// </summary>
public sealed record ScanPreferences
{
    public static readonly ScanPreferences Default = new();

    public ScanRoute Route { get; init; } = ScanRoute.Auto;

    public MediaScanPreferences Nvme { get; init; } = MediaScanPreferences.Auto;

    public MediaScanPreferences SolidState { get; init; } = MediaScanPreferences.Auto;

    public MediaScanPreferences Rotational { get; init; } = MediaScanPreferences.Auto;

    public MediaScanPreferences Removable { get; init; } = MediaScanPreferences.Auto;

    public MediaScanPreferences Network { get; init; } = MediaScanPreferences.Auto;

    public MediaScanPreferences Virtual { get; init; } = MediaScanPreferences.Auto;

    public MediaScanPreferences Unknown { get; init; } = MediaScanPreferences.Auto;

    /// <summary>What the user chose for <paramref name="media"/>.</summary>
    public MediaScanPreferences For(StorageMedia media) => media switch
    {
        StorageMedia.Nvme => Nvme,
        StorageMedia.SolidState => SolidState,
        StorageMedia.Rotational => Rotational,
        StorageMedia.Removable => Removable,
        StorageMedia.Network => Network,
        StorageMedia.Virtual => Virtual,
        StorageMedia.Unknown => Unknown,
        _ => throw new ArgumentOutOfRangeException(nameof(media), media, null),
    };

    /// <summary>These preferences with <paramref name="chosen"/> for <paramref name="media"/>.</summary>
    public ScanPreferences With(StorageMedia media, MediaScanPreferences chosen) => media switch
    {
        StorageMedia.Nvme => this with { Nvme = chosen },
        StorageMedia.SolidState => this with { SolidState = chosen },
        StorageMedia.Rotational => this with { Rotational = chosen },
        StorageMedia.Removable => this with { Removable = chosen },
        StorageMedia.Network => this with { Network = chosen },
        StorageMedia.Virtual => this with { Virtual = chosen },
        StorageMedia.Unknown => this with { Unknown = chosen },
        _ => throw new ArgumentOutOfRangeException(nameof(media), media, null),
    };
}
