using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What one path a tool reads held when it was looked at: whether it was there, its kind, its size,
/// and its creation and last-write times, all from one attribute read.
///
/// <para><b>Both times, not the last-write time alone.</b> A configuration file replaced by a copy
/// can keep the copied file's last-write time, and the copy is a new file with a new creation time.
/// A folder's own last-write time moves when an entry is added, removed or renamed in it, which is
/// what a listing of it is compared by.</para>
/// </summary>
/// <param name="Path">The path as the declaration named it.</param>
/// <param name="Presence">
/// Whether Windows described it. <see cref="PathPresence.Refused"/> makes the fingerprint holding it
/// incomplete: a file nobody could read may hold anything.
/// </param>
/// <param name="IsDirectory">Whether it is a folder, false where it is not there.</param>
/// <param name="Length">Its size in bytes, zero for a folder or where it is not there.</param>
/// <param name="Created">Its creation time as a FILETIME, zero where it is not there.</param>
/// <param name="Written">Its last-write time as a FILETIME, zero where it is not there.</param>
internal readonly record struct InputStamp(
    string Path,
    PathPresence Presence,
    bool IsDirectory,
    long Length,
    long Created,
    long Written)
{
    /// <summary>The path as it stands now.</summary>
    public static InputStamp Of(string path)
    {
        // A relative path would be read against Deguffer's own directory, which the tool never runs
        // in, so it says nothing about what the tool read.
        if (!System.IO.Path.IsPathFullyQualified(path))
        {
            return Unreadable(path);
        }

        var error = FileAttributeRead.Read(LongPath.Extended(path), out var attributes, out var length, out var created, out var written);

        if (error != 0)
        {
            return LongPath.OpenSaysNothingIsThere(error) ? new(path, PathPresence.Absent, false, 0, 0, 0) : Unreadable(path);
        }

        var isDirectory = (attributes & FileAttributes.Directory) != 0;

        return new(path, PathPresence.Present, isDirectory, isDirectory ? 0 : length, created, written);
    }

    /// <summary>A path whose content nobody could read.</summary>
    public static InputStamp Unreadable(string path) => new(path, PathPresence.Refused, false, 0, 0, 0);

    /// <summary>The later of its two times, zero where it is not there.</summary>
    public long Newest => Math.Max(Created, Written);
}
