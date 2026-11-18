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
    ///
    /// <para>A read-only file goes in the one call, without its bit cleared first: package manager
    /// caches set the bit on every file, and a second open for each was most of what removing them
    /// cost. See <see cref="EntryDeletion"/>.</para>
    /// </summary>
    public static RefusalReason? TryDelete(string extendedPath, IFileSystem fs)
    {
        try
        {
            fs.DeleteFile(extendedPath);
            return null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // It went before the deletion reached it. Calling that a refusal would report a file
            // Windows kept that is not on the disk at all.
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Held open by a live process, most often. §5.3: this is the OS protecting state; skip it.
            return RefusalReasons.Of(ex);
        }
    }
}
