using System.Text.RegularExpressions;

namespace Deguffer.Core.Safety;

/// <summary>Reads the attributes of a path, as <see cref="FileAttributeRead.Read(string, out FileAttributes)"/> does.</summary>
/// <returns><c>0</c>, or the Win32 error that says why Windows gave none.</returns>
internal delegate int AttributeReader(string extendedPath, out FileAttributes attributes);

/// <summary>
/// Where the volume holding a path is mounted, answered from the path's own folders where they prove
/// it, and by Windows wherever they do not. What <see cref="IVolumeInventory.MountPointOf"/> answers
/// with on this machine.
///
/// <para><b>Why not Windows every time.</b> <c>GetVolumePathNameW</c> asks the mount manager about
/// every prefix of the path, so its cost grows with the path's depth to about a millisecond for a
/// folder nine levels down, and the in-use check, the executor's last guard and the policies that
/// refuse a volume's top ask it about every path they follow (#512). Reading the attributes of the
/// same folders costs a fraction of that, one read for each.</para>
///
/// <para><b>What the folders prove.</b> Windows answers with the deepest prefix of the path that is
/// a mount point, read through any link on the way. A folder is a mount point, or leads to another
/// volume, only where it is a reparse point. So where the path starts at a local volume's own drive
/// letter, and every folder from that root down is there and is no reparse point, nothing on the way
/// is a mount point and the answer is the root. A folder that is not there ends the proof early:
/// nothing below it exists to be one. Everything else is asked of Windows, and its answer returned unchanged: a
/// reparse point at any level (a junction, a symbolic link, a volume mounted at a folder, a cloud
/// placeholder), a level that would not be read, a <c>subst</c> letter, a mapped share, a share named
/// by its server and a volume named by its GUID.</para>
///
/// <para><b>Live, as the contract requires.</b> Nothing is remembered between calls, so a volume
/// mounted a moment ago is read as one, and <see cref="LiveTreeInspector"/>'s rule that no reachable
/// path is older than the reading it is compared with is unchanged. §5.2 and §5.6 read this answer,
/// through the refusal of a volume's top, the standing-folder guard and the in-use check, which is
/// why any doubt goes to Windows rather than to the cheap answer.</para>
///
/// <para>§6.3: every folder is read in <c>\\?\</c> form, as a prefix of the path's own
/// <see cref="LongPath.Extended"/> form, so no read is cut at <c>MAX_PATH</c>.</para>
/// </summary>
internal sealed partial class MountPointLookup
{
    /// <summary>The one instance the app runs with (G5): nothing here holds state.</summary>
    public static readonly MountPointLookup Machine =
        new(FileAttributeRead.Read, VolumeCalls.DeviceOf, VolumeCalls.VolumePathNameOf);

    /// <summary><c>\\?\</c>, a letter, a colon and a separator: the root of a drive letter's path.</summary>
    private const int DriveRootLength = 7;

    private readonly AttributeReader _read;
    private readonly Func<char, string?> _deviceOf;
    private readonly Func<string, string?> _askWindows;

    /// <param name="read">Reads a folder's attributes.</param>
    /// <param name="deviceOf">What a drive letter stands for in the object manager. See <see cref="VolumeCalls.DeviceOf"/>.</param>
    /// <param name="askWindows">Windows' own answer for an extended path. See <see cref="VolumeCalls.VolumePathNameOf"/>.</param>
    internal MountPointLookup(AttributeReader read, Func<char, string?> deviceOf, Func<string, string?> askWindows)
    {
        _read = read;
        _deviceOf = deviceOf;
        _askWindows = askWindows;
    }

    /// <summary>
    /// The mount point of the volume holding <paramref name="path"/>, in display form, or null where
    /// Windows will not say. See <see cref="IVolumeInventory.MountPointOf"/>.
    /// </summary>
    public string? MountPointOf(string path)
    {
        // Entry rather than Extended alone: it rejects a relative path, rejects one Windows will not
        // accept, and answers null instead of throwing, which is this method's own contract for a
        // path that names no volume.
        if (LongPath.Entry(path) is not { } qualified)
        {
            return null;
        }

        var extended = LongPath.Extended(qualified);

        return ProvenRoot(extended) ?? _askWindows(extended);
    }

    /// <summary>
    /// The drive root of <paramref name="extended"/> in display form, where its letter is a local
    /// volume's own and no folder from that root down is a reparse point, or null where that is not
    /// proved. The root keeps the case it was asked in, as Windows' answer does.
    /// </summary>
    private string? ProvenRoot(string extended)
    {
        if (extended is not ['\\', '\\', '?', '\\', var letter, ':', '\\', ..]
            || !char.IsAsciiLetter(letter)
            || _deviceOf(letter) is not { } device
            || !VolumeDevice().IsMatch(device))
        {
            return null;
        }

        // The root itself first, so a letter whose volume has gone, or a drive with no media in it,
        // is asked of Windows rather than answered by the letter.
        for (var end = DriveRootLength; ; end = NextEnd(extended, end))
        {
            var error = _read(extended[..end], out var attributes);

            if (error == 0)
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return null;
                }

                if (end == extended.Length)
                {
                    return LongPath.Display(extended[..DriveRootLength]);
                }

                continue;
            }

            var absent = error is FileAttributeRead.FileNotFound or FileAttributeRead.PathNotFound;

            return absent && end > DriveRootLength ? LongPath.Display(extended[..DriveRootLength]) : null;
        }
    }

    /// <summary>The end of the prefix one folder deeper than the one ending at <paramref name="end"/>.</summary>
    private static int NextEnd(string extended, int end)
    {
        // The root ends in its separator, and every deeper prefix ends just before one.
        var from = end == DriveRootLength ? end : end + 1;
        var next = extended.IndexOf(Path.DirectorySeparatorChar, from);

        return next < 0 ? extended.Length : next;
    }

    /// <summary>
    /// A volume's own device, and nothing below it. A letter standing for a folder of a volume, which
    /// <c>DefineDosDevice</c> can make, names the device and a folder after it, and its root is not
    /// the volume's top. A shadow copy, an optical drive, a redirector and every other device is
    /// asked of Windows.
    /// </summary>
    [GeneratedRegex(@"^\\Device\\HarddiskVolume[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex VolumeDevice();
}
