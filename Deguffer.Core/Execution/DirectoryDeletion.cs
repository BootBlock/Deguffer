using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// Removes one empty directory, or says why Windows kept it: for a reason of its own, or because
/// something is still inside it.
/// </summary>
internal static class DirectoryDeletion
{
    /// <summary>
    /// ERROR_DIR_NOT_EMPTY: the answer Windows gives for a folder held up by what is still inside it,
    /// as opposed to one it refused for itself.
    /// </summary>
    private const int ErrorDirectoryNotEmpty = 145;

    /// <summary>
    /// Delete one empty directory, and say why it is still there — or null where it is gone: removed
    /// here, or already gone by the time this reached it, which is the same post-condition a removal
    /// reports for a file.
    /// </summary>
    public static Standing? TryDelete(string extendedPath, IFileSystem fs)
    {
        Exception refusal;

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
            // Not empty, in use, refused — or the read-only bit, which the retry below is for.
            refusal = ex;
        }

        // Windows refuses to remove a directory carrying the read-only attribute exactly as it
        // refuses a read-only file, so the file path's retry belongs here too. Without it every
        // file inside such a directory goes, the directory stays, and the step still reports
        // success because bytes were reclaimed — leaving a folder the user was told would go.
        //
        // Which refusal happened is not readable from the exception: .NET reports the same
        // read-only directory as UnauthorizedAccessException for a plain path and as a bare
        // IOException for the extended-length form §6.3 requires, and the HResult goes generic with
        // it. The attributes are the only honest answer, so they are read rather than guessed at.
        // Reading them is also what keeps this away from a link: clearing attributes through a
        // reparse point would act on the far side, which nothing here has classified.
        //
        // Attributes that cannot be read belong to a directory still there, most likely, since the
        // deletion above did not report it missing. So the refusal stands as it was given.
        if (fs.TryGetAttributes(extendedPath) is not { } attributes || !attributes.HasFlag(FileAttributes.ReadOnly))
        {
            return StandingAfter(refusal);
        }

        // Emptiness is asked only of a real directory, and it costs nothing there: clearing the bit
        // on one that still holds something cannot make the removal succeed, so the only thing it
        // could achieve is changing the attributes of a path this removal is leaving standing. A
        // link is the case the question must not be asked of at all — enumerating it reads the far
        // side, which nothing here has classified. Its own attributes are what get cleared, and
        // removing the link without following it is what the caller asked for.
        if (!attributes.HasFlag(FileAttributes.ReparsePoint) && !IsEmpty(extendedPath, fs))
        {
            return Standing.HeldUp;
        }

        try
        {
            fs.ClearAttributes(extendedPath);
            fs.DeleteDirectory(extendedPath);
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Held open, or something arrived in it between the two calls. The read-only bit is
            // cleared and the directory stays, which is the same residue a removal leaves on a file
            // on the same path — and it is a directory this plan named for removal either way.
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
        refusal is IOException io && (io.HResult & 0xFFFF) == ErrorDirectoryNotEmpty
            ? Standing.HeldUp
            : new Standing(RefusalReasons.Of(refusal));

    /// <summary>
    /// Whether the directory holds nothing. Asked only of a directory already known not to be a
    /// link, so the enumeration cannot resolve through one.
    /// </summary>
    private static bool IsEmpty(string extendedPath, IFileSystem fs)
    {
        try
        {
            return fs.EnumerateEntries(extendedPath).Count == 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // Unreadable or already gone: neither is a directory to go on clearing attributes on.
            return false;
        }
    }

    /// <summary>
    /// Why a directory the removal tried to take is still there: the reason Windows gave where it
    /// refused the folder itself, and none where something still inside the folder held it up.
    /// </summary>
    public readonly record struct Standing(RefusalReason? Refused)
    {
        public static Standing HeldUp => new(Refused: null);
    }
}
