using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Roughly when a build directory was last written, for §7's age column.
///
/// <para><b>Not the directory's own timestamp.</b> A directory's <c>LastWriteTime</c> moves only
/// when an entry is added, removed or renamed in it, so a project rebuilt every day reports the date
/// its output layout last changed. The servicing-log provider met the same trap from the other side,
/// where a directory's own timestamp said nothing about a log appended to inside it.</para>
///
/// <para><b>Not the source beside it either</b>, which is the other tempting answer. Age here is
/// asked in order to price a deletion, and what a deletion costs is a rebuild — so "when was this
/// last built" is the question, and "when was this project last edited" is a different one. Reading
/// the source would also mean walking the very tree this exists to avoid walking.</para>
///
/// <para>The newest of the directory's <em>immediate</em> entries, then. That covers the files a
/// build rewrites at the top level and the per-configuration directories whose timestamps move as
/// output is added and removed. It deliberately does not descend: this runs per project across a
/// whole source root, and the column exists to tell a year-old project from this morning's — a
/// resolution that does not justify enumerating hundreds of thousands of files to sharpen.</para>
/// </summary>
internal static class BuildDirectoryAge
{
    public static DateTime? Of(string directory, CancellationToken ct = default)
    {
        try
        {
            DateTime? newest = null;

            foreach (var entry in new DirectoryInfo(LongPath.Extended(directory)).EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();

                if (newest is null || entry.LastWriteTimeUtc > newest)
                {
                    newest = entry.LastWriteTimeUtc;
                }
            }

            return newest;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // No timestamp is a real answer, and RelativeAge renders it as unknown rather than as
            // an age.
            return null;
        }
    }
}
