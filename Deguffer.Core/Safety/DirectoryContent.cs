namespace Deguffer.Core.Safety;

/// <summary>
/// §5.6's question about a protected directory: whether anything worth protecting is still in it.
///
/// <para><b>Content is a file, a link, or a directory that would not be listed, at any depth.</b> A
/// folder holding only empty folders holds nothing, because a removal that takes every file and
/// leaves the folders that held them has taken everything a protection was for. Asked of the top
/// level alone, that folder still holds a folder, and the loss reads as a survivor. A File History
/// account's folder is the shape that exposed it: it still holds its machine's folder after every
/// saved version below it has gone.</para>
///
/// <para><b>A link counts, and is never followed.</b> It is an entry somebody put there, and what it
/// points at is a tree nothing classified — the rule <see cref="Scanning.BoundedFileWalk"/> holds,
/// for the same reason.</para>
///
/// <para><b>A refusal below the directory counts, and a refusal of the directory itself does
/// not.</b> A subdirectory that will not be listed was seen, so something is there, and reading it
/// as empty would turn a refusal Deguffer meets every day (§5.3) into an alarm the moment the files
/// beside it went. The directory itself refusing is the opposite case: nothing was seen at all, so
/// it is never recorded as having held something, which keeps a refusal out of the evidence rather
/// than making one of it.</para>
///
/// <para><b>It stops at the first content it finds</b> (G4), so a tree with a file near the top
/// costs a listing or two. The expensive shape is a deep tree of nothing but empty folders, which is
/// walked whole, because that is exactly the tree the answer has to be about.</para>
///
/// <para>§6.3: the walk starts from the extended-length form, and .NET builds every child from the
/// parent it was given. The answer is a boolean, so no test can observe that form; the test past
/// <c>MAX_PATH</c> proves the walk reaches content that deep, and no more.</para>
///
/// <para><b>Two questions, and what ran decides which is asked afterwards.</b>
/// <see cref="IsPresent"/> looks through folders, and <see cref="HoldsAnyEntry"/> stops at the top
/// level. What a protected directory held before a run is always captured with the first. After the
/// run, looking through folders is only evidence where a tool's own command ran, because a tool is
/// what empties files in place and leaves their folders standing. Deguffer's own deletions take a
/// folder with its files, so without a command a folder left holding only empty folders was left that
/// way by something else, and the second question is the one asked. See
/// <c>PlanVerifier.WasEmptied</c>.</para>
/// </summary>
public static class DirectoryContent
{
    /// <summary>
    /// Whether <paramref name="path"/> is a directory holding any entry at all at its top level, an
    /// empty folder included. False for a file, for a path that is not there, and for a directory
    /// that cannot be listed, which is the answer <see cref="IsPresent"/> gives for the same three.
    /// </summary>
    public static bool HoldsAnyEntry(string path)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(LongPath.Extended(path)).Any();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Not a directory, not there, or refused: nothing was seen.
            return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> is a directory with content anywhere below it. False for a
    /// file, for a path that is not there, and for a directory that cannot be listed.
    /// </summary>
    public static bool IsPresent(string path)
    {
        var root = LongPath.Extended(path);
        var pending = new Queue<string>([root]);

        while (pending.TryDequeue(out var directory))
        {
            try
            {
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    if (entry is not DirectoryInfo || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        return true;
                    }

                    pending.Enqueue(entry.FullName);
                }
            }
            catch (DirectoryNotFoundException)
            {
                // The path is not there, or a subdirectory went between being listed and being read.
                // Neither is content.
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Refused, or not a directory at all. Below the root, a refused subdirectory is
                // something seen and not readable; at the root, nothing was seen.
                if (directory != root)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
