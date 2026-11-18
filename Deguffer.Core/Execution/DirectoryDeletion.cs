using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// Removes one empty directory, or says why Windows kept it: for a reason of its own, or because
/// something is still inside it.
/// </summary>
internal static class DirectoryDeletion
{
    /// <summary>
    /// Delete one empty directory, and say why it is still there — or null where it is gone: removed
    /// here, or already gone by the time this reached it, which is the same post-condition a removal
    /// reports for a file.
    ///
    /// <para>A read-only directory goes too, and one refused keeps its bit: see
    /// <see cref="EntryDeletion"/>. One left standing for its bit would be a folder the user was told
    /// would go, in a step that still reports success because bytes were reclaimed.</para>
    /// </summary>
    public static Standing? TryDelete(string extendedPath, IFileSystem fs)
    {
        try
        {
            fs.DeleteDirectory(extendedPath);
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Not empty, in use, or refused.
            return StandingAfter(ex);
        }
    }

    /// <summary>
    /// What a refusal to remove a directory says about why it stayed.
    ///
    /// <para>Windows answers a folder that still holds something with an error of its own, so that
    /// case is told apart from a folder refused for itself. Observed on Windows 11 for the
    /// extended-length form §6.3 requires: ERROR_DIR_NOT_EMPTY for a folder holding a folder, a
    /// sharing violation for a folder another process has as its working directory, and a bare
    /// IOException for a folder the account is denied.</para>
    /// </summary>
    private static Standing StandingAfter(Exception refusal) =>
        refusal is IOException io && (io.HResult & 0xFFFF) == Win32Error.DirectoryNotEmpty
            ? Standing.HeldUp
            : new Standing(RefusalReasons.Of(refusal));

    /// <summary>
    /// Why a directory the removal tried to take is still there: the reason Windows gave where it
    /// refused the folder itself, and none where something still inside the folder held it up.
    /// </summary>
    public readonly record struct Standing(RefusalReason? Refused)
    {
        public static Standing HeldUp => new(Refused: null);
    }
}
