using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <param name="BytesReclaimed">The file's length, or zero if it was left in place.</param>
/// <param name="Skipped">1 when something held the file open (§5.3), 0 otherwise.</param>
/// <param name="Removed">Whether the file is gone.</param>
public sealed record FileRemovalOutcome(long BytesReclaimed, int Skipped, bool Removed);

/// <summary>
/// Deletes one named file.
///
/// Separate from <see cref="DirectoryRemover"/> rather than a mode of it, because the two have
/// different failure shapes and only one of them can partially succeed. A tree removal walks,
/// deletes what it can and reports what it skipped; this either removes the one path it was given
/// or does not.
///
/// §6.3: the path goes through the extended-length prefix, and §5.3: a file something else holds
/// open is skipped rather than escalated. A link is removed as a link and never followed, which
/// here means the target is never touched — the same rule <see cref="DirectoryRemover"/> applies to
/// its own root.
/// </summary>
public static class FileRemover
{
    /// <param name="fileSystem">
    /// Defaults to the real filesystem, and injectable for the same reason
    /// <see cref="DirectoryRemover"/>'s is: §6.3's requirement is about the *form* of the path that
    /// crosses into Win32, which no outcome can demonstrate.
    /// </param>
    public static Task<FileRemovalOutcome> RemoveAsync(
        string path,
        CancellationToken ct = default,
        IFileSystem? fileSystem = null) =>
        Task.Run(() => Remove(path, fileSystem ?? WindowsFileSystem.Default), ct);

    private static FileRemovalOutcome Remove(string path, IFileSystem fs)
    {
        var extended = LongPath.Extended(path);

        // Measured before the deletion, because afterwards there is nothing to ask. Zero for a file
        // that has already gone, which is then reported as removed rather than as skipped: the
        // post-condition the caller cares about holds either way.
        var length = fs.TryGetFileLength(extended);

        if (length is null)
        {
            // Either it went between planning and now, or something that is not a file has taken
            // the name. Neither is this step's to act on, and only the first counts as removed.
            return new FileRemovalOutcome(0, 0, Removed: !fs.DirectoryExists(extended));
        }

        // A link is deleted as a link by File.Delete, so what it points at is untouched — but the
        // length above is the link's own, not the target's, and reporting the target's size as
        // reclaimed would overstate the run.
        if (fs.IsReparsePoint(extended))
        {
            fs.DeleteFile(extended);
            return new FileRemovalOutcome(0, 0, Removed: true);
        }

        try
        {
            fs.DeleteFile(extended);
        }
        catch (UnauthorizedAccessException)
        {
            // Commonly the read-only bit; MEMORY.DMP is written with it set on some configurations.
            try
            {
                fs.ClearAttributes(extended);
                fs.DeleteFile(extended);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return new FileRemovalOutcome(0, 1, Removed: false);
            }
        }
        catch (IOException)
        {
            // Held open — a dump still being written, most likely. §5.3: skip it.
            return new FileRemovalOutcome(0, 1, Removed: false);
        }

        return new FileRemovalOutcome(length.Value, 0, Removed: true);
    }
}
