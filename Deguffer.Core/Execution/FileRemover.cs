using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <param name="BytesReclaimed">The file's length, or zero if it was left in place.</param>
/// <param name="Refused">
/// The file and its length when Windows would not release it, by reason, and nothing otherwise.
/// §5.3 makes that ordinary rather than a fault. The reason is the one the deletion reported: a file
/// another program holds open, or a refusal of any other kind — see <see cref="RefusalReason"/>.
/// </param>
/// <param name="Removed">Whether the file is gone.</param>
/// <param name="Kept">
/// Whether it was left alone because the user asked for anything touched recently to be left. A
/// separate answer from <paramref name="Refused"/> for the reason
/// <see cref="RemovalOutcome.Kept"/> gives: one is Windows refusing and the other is Deguffer
/// obeying, and they are different sentences to whoever reads the result.
/// </param>
/// <param name="Took">
/// Whether this removal is what took the file. False where it was already gone, or went before the
/// deletion reached it: <paramref name="Removed"/> is still true then, because the path is gone, and
/// nothing was taken. See <see cref="RemovalOutcome.EntriesRemoved"/>.
/// </param>
/// <param name="MailStore">
/// Whether it was left alone because it is an Outlook mail store, which Deguffer never removes. A
/// fourth answer rather than a kind of <paramref name="Kept"/>, because it is neither a setting the
/// user chose nor Windows refusing, and the sentence that reports it says which.
/// </param>
public sealed record FileRemovalOutcome(
    long BytesReclaimed,
    Refusals Refused,
    bool Removed,
    bool Kept = false,
    bool Took = false,
    bool MailStore = false);

/// <summary>
/// Deletes one named file.
///
/// Separate from <see cref="DirectoryRemover"/> rather than a mode of it, because the two have
/// different failure shapes and only one of them can partially succeed. A tree removal walks,
/// deletes what it can and reports what it was refused; this either removes the one path it was
/// given or does not.
///
/// §6.3: the path goes through the extended-length prefix, and §5.3: a file Windows will not
/// release is left rather than escalated. A link is removed as a link and never followed, which here
/// means the target is never touched — the same rule <see cref="DirectoryRemover"/> applies to its
/// own root. An Outlook mail store is never removed, whatever mark it carries (§9).
/// </summary>
public static class FileRemover
{
    /// <param name="fileSystem">
    /// Defaults to the real filesystem, and injectable for the same reason
    /// <see cref="DirectoryRemover"/>'s is: §6.3's requirement is about the *form* of the path that
    /// crosses into Win32, which no outcome can demonstrate.
    /// </param>
    /// <param name="keep">
    /// The user's guard on recently touched files. Asked here as well as when the step was planned,
    /// because the two moments are minutes apart and the file may have been written in between —
    /// the same reason §7.1 has Explore decide a refusal again at the point of deletion.
    /// </param>
    public static Task<FileRemovalOutcome> RemoveAsync(
        string path,
        MinimumAge keep = default,
        CancellationToken ct = default,
        IFileSystem? fileSystem = null) =>
        Task.Run(() => Remove(path, keep, fileSystem ?? WindowsFileSystem.Default), ct);

    private static FileRemovalOutcome Remove(string path, MinimumAge keep, IFileSystem fs)
    {
        var extended = LongPath.Extended(path);

        // §9: an Outlook mail store is never removed, whoever named it. Asked before the link branch,
        // because the mark Windows puts on a link is also on files that are not links — a OneDrive
        // placeholder, a deduplicated file — and deleting one of those deletes its content, so a file
        // named like a store is left whatever it carries. Asked before the guard, because the rule is
        // unconditional and the guard is a preference. A folder that has taken the name is not a store,
        // and the branches below answer it.
        if (MailStore.Is(extended) && !fs.DirectoryExists(extended))
        {
            return new FileRemovalOutcome(0, Refusals.None, Removed: false, MailStore: true);
        }

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
            return Delete(extended, fs, length: 0);
        }

        // Something that is not a file has taken the name. Not this step's to remove, and the
        // read-only retry below would otherwise clear a directory's own attributes on the way to
        // failing anyway.
        if (fs.DirectoryExists(extended))
        {
            return new FileRemovalOutcome(0, Refusals.None, Removed: false);
        }

        // The guard, on the file this step actually names. Asked after the link and directory
        // branches above, so the timestamp read is the one belonging to the thing being removed.
        if (fs.TryGetNewestFileTime(extended) is { } newest && keep.Protects(newest))
        {
            return new FileRemovalOutcome(0, Refusals.None, Removed: false, Kept: true);
        }

        // Measured before the deletion, because afterwards there is nothing to ask. An unknown
        // length covers both "already gone" and "we were refused", and the deletion is what
        // separates them: removing a path that is not there succeeds silently, and one we may not
        // touch throws. Deciding it here from an existence check instead reported "Removed." for a
        // file that was still on the disk, because a refusal and an absence look the same to one.
        // Once the deletion has succeeded, though, an unknown length can only have been an absence,
        // and that is what says this removal took nothing.
        return Delete(extended, fs, fs.TryGetFileLength(extended));
    }

    /// <param name="length">The file's length, or null where no file was there to measure.</param>
    private static FileRemovalOutcome Delete(string extended, IFileSystem fs, long? length)
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
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // It went between the two attempts, which is the race the arm below answers too.
                return new FileRemovalOutcome(0, Refusals.None, Removed: true);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return new FileRemovalOutcome(0, Refusals.One(RefusalReasons.Of(ex), length ?? 0), Removed: false);
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // It went between the probe and the delete. The post-condition holds, so this is the
            // same success DirectoryRemover.TryDeleteFile reports for the identical race — and
            // DirectoryNotFoundException derives from IOException, so without this arm the catch
            // below would call a file that is gone "left in place".
            return new FileRemovalOutcome(0, Refusals.None, Removed: true);
        }
        catch (IOException ex)
        {
            // Held open — a dump still being written, most likely. §5.3: leave it.
            return new FileRemovalOutcome(0, Refusals.One(RefusalReasons.Of(ex), length ?? 0), Removed: false);
        }

        return new FileRemovalOutcome(length ?? 0, Refusals.None, Removed: true, Took: length is not null);
    }
}
