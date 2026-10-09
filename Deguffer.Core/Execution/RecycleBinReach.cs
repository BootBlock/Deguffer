using System.Security;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// Whether Windows' Recycle Bin can take an item, which the shell decides by the length of the
/// item's path and of every path inside it.
///
/// <para><b>Measured on 2026-10-09</b>, on NTFS with long paths enabled, through
/// <c>IFileOperation</c> with <c>FOFX_RECYCLEONDELETE</c> set: a file whose path was 259 characters
/// went to the bin, and one of 260 or more was deleted outright, the operation reporting success and
/// handing its progress sink no bin item. A folder holding one such path anywhere inside it was
/// deleted outright whole, its own short path and every short path beside it included, while a path
/// that grew past 259 only once its folder was in the bin did not matter. So the shell is never
/// asked to move an item this refuses, because §7.1 and §7.4 forbid deleting outright what the user
/// asked to send to the bin.</para>
///
/// <para>Asked of the item as it is at the moment of the call. A path made longer between this and
/// the shell's move is caught after the fact (<see cref="RecycleOutcome.DeletedOutright"/>), which
/// can report the loss and not undo it.</para>
/// </summary>
internal static class RecycleBinReach
{
    /// <summary>The longest path the shell moves to the bin: <c>MAX_PATH</c> less its terminator.</summary>
    internal const int LongestPath = 259;

    /// <summary>Why the bin cannot take the item at <paramref name="path"/>, or null where it can.</summary>
    /// <param name="path">The item in display form, as the shell is handed it.</param>
    internal static string? WhyNot(string path)
    {
        if (path.Length > LongestPath)
        {
            return $"Its path is {path.Length:N0} characters long. {TakesNoLongerPath}, and deletes "
                + "it outright instead, so Deguffer did not ask. It is still where it was.";
        }

        FileAttributes attributes;

        try
        {
            attributes = File.GetAttributes(LongPath.Extended(path));
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

        // A link is moved as a link, and nothing on its far side goes with it, so only a folder that
        // is not one is looked inside.
        if (!attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return null;
        }

        Stack<string> folders = new([LongPath.Extended(path)]);

        while (folders.TryPop(out var folder))
        {
            try
            {
                foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
                {
                    var shown = LongPath.Display(entry.FullName);

                    if (shown.Length > LongestPath)
                    {
                        return $"It holds '{shown[(path.Length + 1)..]}', whose path is {shown.Length:N0} characters long. "
                            + $"{TakesNoLongerPath}, and deletes the whole folder outright instead, so Deguffer did "
                            + "not ask. It is still where it was.";
                    }

                    if (entry.Attributes.HasFlag(FileAttributes.Directory) && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        folders.Push(entry.FullName);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                return $"Windows would not list '{LongPath.Display(folder)}', so Deguffer cannot tell whether every path "
                    + "in the folder is short enough for the Recycle Bin, which deletes outright what it cannot take. "
                    + "It is still where it was.";
            }
        }

        return null;
    }

    private static readonly string TakesNoLongerPath = $"Windows' Recycle Bin takes nothing whose path is longer than {LongestPath}";
}
