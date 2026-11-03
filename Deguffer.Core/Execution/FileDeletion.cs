using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// Removes one file a tree removal's walk listed, or says why Windows kept it. The file counterpart
/// of <see cref="DirectoryDeletion"/>.
/// </summary>
internal static class FileDeletion
{
    /// <summary>
    /// Delete one file, and say why Windows would not let it go — or null where it went, or where it
    /// had already gone.
    /// </summary>
    /// <param name="attributes">
    /// The file's attributes as the walk's listing read them. A read-only bit there is cleared before
    /// the delete rather than after Windows refuses it: package manager caches set the bit on every
    /// file, and each refusal is a thrown exception and a second delete.
    /// </param>
    public static RefusalReason? TryDelete(string extendedPath, FileAttributes attributes, IFileSystem fs)
    {
        try
        {
            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                fs.ClearAttributes(extendedPath);
            }

            fs.DeleteFile(extendedPath);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // The read-only bit, set since the walk listed the file, or a refusal of another kind
            // that clearing it cannot change and the second attempt reports.
            try
            {
                fs.ClearAttributes(extendedPath);
                fs.DeleteFile(extendedPath);
                return null;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // It went between the two attempts. Calling that a refusal would report a file
                // Windows kept that is not on the disk at all.
                return null;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // The second answer is the one that counts: a read-only file something holds open
                // refuses first for the bit and then for the handle.
                return RefusalReasons.Of(ex);
            }
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException ex)
        {
            // Held open by a live process, most often. §5.3: this is the OS protecting state; skip it.
            return RefusalReasons.Of(ex);
        }
    }
}
