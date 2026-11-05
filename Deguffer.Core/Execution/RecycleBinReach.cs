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

    internal const string DeletesWhatItCannotTake = "Windows deletes outright what its bin cannot take.";

    internal static readonly string TakesNoLongerPath =
        $"Windows' Recycle Bin takes nothing whose path is longer than {LongestPath}";

    // The walk a removal makes of its tree is on the default tuning too (RemovalWalk), and this walk is
    // of the tree that removal would move.
    public static RecycleBinReach Default { get; } =
        new(VolumeInventory.Current, RecycleBinRooms.Default, WindowsFileSystem.Default, WalkTuning.Default);

    private readonly IVolumeInventory _volumes;
    private readonly RecycleBinRooms _rooms;
    private readonly IFileSystem _fs;
    private readonly WalkTuning _tuning;

    /// <param name="volumes">Which volume holds an item, whose bin is the one it would go to.</param>
    /// <param name="rooms">What each volume's bin may hold.</param>
    /// <param name="fs">What a folder is listed through, which a test observes and refuses through.</param>
    /// <param name="tuning">How many of a folder's folders are listed at once.</param>
    internal RecycleBinReach(IVolumeInventory volumes, RecycleBinRooms rooms, IFileSystem fs, WalkTuning tuning)
    {
        _volumes = volumes;
        _rooms = rooms;
        _fs = fs;
        _tuning = tuning;
    }

    /// <summary>
    /// Why the bin cannot take the item at <paramref name="path"/>, or null where it can: the reason
    /// alone, which a confirmation states before anything is asked and a refusal after, each adding
    /// what became of the item.
    /// </summary>
    /// <param name="path">The item in display form, as the shell is handed it.</param>
    /// <param name="ct">Stops the walk of a folder, which is of everything inside it.</param>
    internal string? WhyNot(string path, CancellationToken ct = default)
    {
        if (NameTheShellRereads(path) is { } name)
        {
            return $"'{name}' on its path ends in a dot or a space, and the Recycle Bin reads such a name as "
                + "another, so it would move something else or nothing.";
        }

        if (path.Length > LongestPath)
        {
            return $"Its path is {path.Length:N0} characters long. {TakesNoLongerPath}, and deletes it outright instead.";
        }

        string extended;
        FileAttributes attributes;
        long length = 0;

        try
        {
            extended = LongPath.Extended(path);
            attributes = File.GetAttributes(extended);

            if (!attributes.HasFlag(FileAttributes.Directory))
            {
                length = new FileInfo(extended).Length;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Nothing there to lose; the shell reports it gone in its own words.
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return "Windows would not describe it, so Deguffer cannot tell whether the Recycle Bin can take it "
                + $"whole, and {DeletesWhatItCannotTake}";
        }

        // Before the walk, because a bin that refuses the item whatever it holds is answered without
        // reading a folder of any size.
        if (WhyTheBinTakesNothing(path, out var limit) is { } refused)
        {
            return refused;
        }

        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            return length > limit
                ? $"It is {FreeSpace.Format(length)} long, more than this drive's Recycle Bin can hold "
                  + $"({FreeSpace.Format(limit)}), and Windows deletes outright what is larger than its bin."
                : null;
        }

        // A folder that is itself a link is moved as a link, and nothing on its far side goes with it.
        return attributes.HasFlag(FileAttributes.ReparsePoint)
            ? null
            : RecycleBinFolderWalk.WhyNot(extended, limit, _fs, _tuning, ct);
    }

    /// <summary>
    /// The first name on <paramref name="path"/> that ends in a dot or a space, or null where none
    /// does.
    ///
    /// <para><b>Observed on 2026-10-10:</b> <c>SHCreateItemFromParsingName</c>, which every
    /// removal to the bin parses its path through, drops trailing dots and spaces from the last name,
    /// and trailing dots from every other. Beside <c>report</c>, a <c>report.</c> parsed as
    /// <c>report</c>, and <c>sub.\x.txt</c> and <c>sub..\x.txt</c> as <c>sub\x.txt</c>; a
    /// <c>notes </c> parsed as nothing at all, while <c>sp \y.txt</c> kept its space. NTFS keeps
    /// such names apart, and the shell takes no <c>\\?\</c> path that would spell them, so the bin
    /// cannot be handed the item at all.</para>
    /// </summary>
    private static string? NameTheShellRereads(string path)
    {
        var names = path[(Path.GetPathRoot(path)?.Length ?? 0)..]
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < names.Length; i++)
        {
            if (names[i] is not ("." or "..") && (names[i].EndsWith('.') || (i == names.Length - 1 && names[i].EndsWith(' '))))
            {
                return names[i];
            }
        }

        return null;
    }

    /// <summary>
    /// Why the bin of the volume holding <paramref name="path"/> takes nothing whatever the item holds,
    /// or cannot be shown to take anything; or null, with the most it may hold in
    /// <paramref name="limit"/>.
    /// </summary>
    private string? WhyTheBinTakesNothing(string path, out long limit)
    {
        const string Unknown = $", so nothing shows the Recycle Bin can take it, and {DeletesWhatItCannotTake}";

        limit = 0;

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
                + "delete this outright.";
        }

        if (room.Limit is not { } most)
        {
            return "Windows would not say how much this drive's Recycle Bin may hold" + Unknown;
        }

        limit = most;
        return null;
    }
}
