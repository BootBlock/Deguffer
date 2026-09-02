using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// When a directory was last written to, one level down, for §7's age column.
///
/// <para>One rule, because it is one fact. It was written three times before this — for build
/// output, for a declared log or dump folder, and for a VS Code workspace database — each time with
/// its own paragraph deriving the same reasoning, and the three had drifted into three different
/// answers. What each caller genuinely owns is <em>whether</em> its subject has a meaningful single
/// age at all, and that decision stays with the caller.</para>
///
/// <para><b>The newest of the directory's own timestamp and its immediate entries'.</b> Neither half
/// answers alone, and both halves fail in the direction that invites a deletion:</para>
///
/// <list type="bullet">
/// <item>NTFS moves a directory's own timestamp when an entry is added, removed or renamed, and
/// leaves it alone when an entry's contents change. So the directory alone reports the date its
/// layout last changed — the age of a project's first build, however often it is rebuilt into the
/// same set of files, and the age of a servicing log directory whose log is being appended to right
/// now.</item>
/// <item>The entries alone miss everything the directory catches. A build that prunes stale output
/// touches the directory and nothing that stays in it, so the answer is the age of what was left
/// behind. An emptied directory has no entries at all and answers "unknown", which §7 renders as a
/// blank column — a project cleaned an hour ago made indistinguishable from one that could not be
/// read.</item>
/// </list>
///
/// <para><b>One level, deliberately.</b> This runs per project across a whole source root, and the
/// column exists to tell a year-old project from this morning's. That resolution does not justify
/// enumerating hundreds of thousands of files to sharpen, and for build output it would mean walking
/// the very tree discovery went out of its way not to walk. Where a location's content nests below
/// this level, the answer is not to walk deeper but to report no age — see
/// <see cref="DeclaredLocation.ReportsAge"/> for a Maven repository, whose top level moves only when
/// a whole new group first appears.</para>
///
/// <para><b>Not the source beside it either</b>, which is the other tempting answer for build
/// output. An age is asked in order to price a deletion, and what a deletion costs is a rebuild — so
/// "when was this last built" is the question and "when was this project last edited" is a different
/// one.</para>
/// </summary>
internal static class DirectoryAge
{
    public static DateTime? Of(string directory, CancellationToken ct = default)
    {
        var info = new DirectoryInfo(LongPath.Extended(directory));

        if (!info.Exists)
        {
            // LastWriteTimeUtc answers for a missing path with the start of the Windows epoch rather
            // than failing, and January 1601 in the age column is the oldest invitation there is to
            // delete something that has already gone.
            return null;
        }

        var newest = info.LastWriteTimeUtc;

        try
        {
            foreach (var entry in info.EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();

                if (entry.LastWriteTimeUtc > newest)
                {
                    newest = entry.LastWriteTimeUtc;
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // §5.3 makes a refusal ordinary, and the directory's own timestamp is still a real
            // answer — one that can only read newer than the truth, which is the direction that
            // discourages a deletion rather than inviting one.
        }

        return newest;
    }
}
