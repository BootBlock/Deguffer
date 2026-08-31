using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <param name="BytesReclaimed">The file's length, or zero if it was left in place.</param>
/// <param name="Skipped">
/// 1 when the file was left in place, 0 otherwise. §5.3 makes that ordinary rather than a fault,
/// and it names no cause: a file held open and one this process may not touch are the same answer
/// from here, which is the distinction <see cref="PlanExecutor"/> is careful not to assert either.
/// </param>
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

        // A link is removed as a link, and nothing on the far side counts as reclaimed.
        //
        // This changes no outcome on Windows today, which is worth saying rather than implying:
        // File.Delete already removes a link instead of what it points at, and FileInfo.Length
        // already reports the link's own zero rather than the target's — measured here for a live
        // link and a dangling one alike. The branch is kept because both of those are the
        // platform's behaviour and not this code's, and a safety property riding on an unstated one
        // is exactly how the shader caches came to enumerate through a junction. Stated here, the
        // zero and the link-not-target removal are decisions a reader can check.
        if (fs.IsReparsePoint(extended))
        {
            return Delete(extended, fs, reclaimed: 0);
        }

        // Something that is not a file has taken the name. Not this step's to remove, and the
        // read-only retry below would otherwise clear a directory's own attributes on the way to
        // failing anyway.
        if (fs.DirectoryExists(extended))
        {
            return new FileRemovalOutcome(0, 0, Removed: false);
        }

        // Measured before the deletion, because afterwards there is nothing to ask. An unknown
        // length covers both "already gone" and "we were refused", and the deletion is what
        // separates them: removing a path that is not there succeeds silently, and one we may not
        // touch throws. Deciding it here from an existence check instead reported "Removed." for a
        // file that was still on the disk, because a refusal and an absence look the same to one.
        return Delete(extended, fs, fs.TryGetFileLength(extended) ?? 0);
    }

    private static FileRemovalOutcome Delete(string extended, IFileSystem fs, long reclaimed)
    {
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
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // It went between the probe and the delete. The post-condition holds, so this is the
            // same success DirectoryRemover.TryDeleteFile reports for the identical race — and
            // DirectoryNotFoundException derives from IOException, so without this arm the catch
            // below would call a file that is gone "left in place".
            return new FileRemovalOutcome(0, 0, Removed: true);
        }
        catch (IOException)
        {
            // Held open — a dump still being written, most likely. §5.3: skip it.
            return new FileRemovalOutcome(0, 1, Removed: false);
        }

        return new FileRemovalOutcome(reclaimed, 0, Removed: true);
    }
}
