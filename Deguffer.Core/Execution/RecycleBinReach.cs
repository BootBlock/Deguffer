using System.Security;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// Whether Windows' Recycle Bin can take an item: the shell deletes outright, and reports success,
/// anything the bin cannot hold, so such an item is never handed to it (§7.1, §7.4: a removal the bin
/// will not take fails, and is never deleted outright in its place).
///
/// <para><b>Measured on 2026-10-09</b>, on NTFS with long paths enabled, through
/// <c>IFileOperation</c> with <c>FOFX_RECYCLEONDELETE</c> set, which did not stop any of these:</para>
/// <list type="bullet">
/// <item>A file whose path was 259 characters went to the bin, and one of 260 or more was deleted
/// outright. A folder holding one such path anywhere inside it was deleted outright whole, its own
/// short path and every short path beside it included, while a path that grew past 259 only once its
/// folder was in the bin did not matter.</item>
/// <item>A file as long as the volume's bin limit (<see cref="RecycleBinRoom.Limit"/>) went to the
/// bin, and one byte longer was deleted outright, compressed to nothing on disk or not, so the length
/// is what is compared. A folder whose files were each shorter than the limit and together longer was
/// deleted outright whole.</item>
/// <item>With the bin set to keep nothing (<see cref="RecycleBinRoom.KeepsNothing"/>), a file of one
/// line was deleted outright.</item>
/// </list>
///
/// <para>Asked of the item as it is at the moment of the call, and of the bin as its settings say,
/// where they can be read. Where they cannot (a drive this cannot place, a bin Windows will not
/// describe, a limit it will not say, or a removable drive with no bin at all), nothing shows the bin
/// can take the item, so it is refused: the shell would delete it outright if it could not. What
/// changes between this and the shell's move, or a reason this does not know, is caught after the
/// fact (<see cref="RecycleOutcome.DeletedOutright"/>), which can report the loss and not undo it.</para>
///
/// <para>Stateless apart from its collaborators, so one instance serves the process (G5).</para>
/// </summary>
internal sealed class RecycleBinReach
{
    /// <summary>The longest path the shell moves to the bin: <c>MAX_PATH</c> less its terminator.</summary>
    internal const int LongestPath = 259;

    public static RecycleBinReach Default { get; } = new(VolumeInventory.Current, RecycleBinRooms.Default);

    private static readonly string TakesNoLongerPath =
        $"Windows' Recycle Bin takes nothing whose path is longer than {LongestPath}";

    private readonly IVolumeInventory _volumes;
    private readonly RecycleBinRooms _rooms;

    /// <param name="volumes">Which volume holds an item, whose bin is the one it would go to.</param>
    /// <param name="rooms">What each volume's bin may hold.</param>
    internal RecycleBinReach(IVolumeInventory volumes, RecycleBinRooms rooms)
    {
        _volumes = volumes;
        _rooms = rooms;
    }

    /// <summary>Why the bin cannot take the item at <paramref name="path"/>, or null where it can.</summary>
    /// <param name="path">The item in display form, as the shell is handed it.</param>
    internal string? WhyNot(string path)
    {
        if (path.Length > LongestPath)
        {
            return $"Its path is {path.Length:N0} characters long. {TakesNoLongerPath}, and deletes "
                + "it outright instead, so Deguffer did not ask. It is still where it was.";
        }

        FileSystemInfo item;

        try
        {
            var extended = LongPath.Extended(path);
            var attributes = File.GetAttributes(extended);
            item = attributes.HasFlag(FileAttributes.Directory) ? new DirectoryInfo(extended) : new FileInfo(extended);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Nothing there to lose; the shell reports it gone in its own words.
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return "Windows would not describe it, so Deguffer cannot tell whether the Recycle Bin can take it "
                + "whole. It is still where it was.";
        }

        var (length, inside) = item is DirectoryInfo folder ? Walk(path, folder) : (((FileInfo)item).Length, null);

        return inside ?? WhyTheBinCannotHold(path, length);
    }

    /// <summary>
    /// The length of every file in <paramref name="folder"/>, or why the bin cannot take it whatever
    /// its length. A link inside is moved as a link, and nothing on its far side goes with it, so it is
    /// not looked through; a folder that is itself a link is moved the same way.
    /// </summary>
    private static (long Length, string? WhyNot) Walk(string path, DirectoryInfo folder)
    {
        if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return (0, null);
        }

        long length = 0;
        Stack<string> folders = new([folder.FullName]);

        while (folders.TryPop(out var next))
        {
            try
            {
                foreach (var entry in new DirectoryInfo(next).EnumerateFileSystemInfos())
                {
                    var shown = LongPath.Display(entry.FullName);

                    if (shown.Length > LongestPath)
                    {
                        return (length, $"It holds '{shown[(path.Length + 1)..]}', whose path is {shown.Length:N0} characters long. "
                            + $"{TakesNoLongerPath}, and deletes the whole folder outright instead, so Deguffer did "
                            + "not ask. It is still where it was.");
                    }

                    if (entry is FileInfo file)
                    {
                        length += file.Length;
                    }
                    else if (!entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        folders.Push(entry.FullName);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                return (length, $"Windows would not list '{LongPath.Display(next)}', so Deguffer cannot tell whether "
                    + "the Recycle Bin can take the folder whole, and it deletes outright what it cannot take. "
                    + "It is still where it was.");
            }
        }

        return (length, null);
    }

    /// <summary>
    /// Why the bin of the volume holding <paramref name="path"/> cannot hold <paramref name="length"/>
    /// bytes, or cannot be shown to, or null where it can.
    /// </summary>
    private string? WhyTheBinCannotHold(string path, long length)
    {
        const string Unknown = ", so nothing shows the Recycle Bin can take it, and Windows deletes outright what its bin "
            + "cannot take. Deguffer did not ask, and it is still where it was.";

        if (HostVolume.For(_volumes, path) is not { } volume)
        {
            return "Deguffer cannot tell which drive this is on" + Unknown;
        }

        if (_rooms.Of(volume) is not { } room)
        {
            return "Windows would not say what this drive's Recycle Bin holds, or the drive has none" + Unknown;
        }

        if (room.KeepsNothing)
        {
            return "This drive's Recycle Bin is set to delete what it is sent rather than keep it, so Windows would "
                + "delete this outright, and Deguffer did not ask. It is still where it was.";
        }

        if (room.Limit is not { } limit)
        {
            return "Windows would not say how much this drive's Recycle Bin may hold" + Unknown;
        }

        return length > limit
            ? $"It is {FreeSpace.Format(length)} long, more than this drive's Recycle Bin can hold "
              + $"({FreeSpace.Format(limit)}), and Windows deletes outright what is larger than its bin, so Deguffer "
              + "did not ask. It is still where it was."
            : null;
    }
}
