using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Whether a file is an SQLite database that nothing has open, judged by its header and by the
/// files SQLite writes beside it, as a listing of its folder found them.
///
/// <para><b>The header is the format's own.</b> Every SQLite database opens with the sixteen bytes
/// <c>SQLite format 3\0</c> (<see href="https://www.sqlite.org/fileformat.html">the file format</see>),
/// so a file carrying a database's name and anything else is not the database it is named for.</para>
///
/// <para><b>A journal beside it declines it.</b> SQLite writes <c>-journal</c>, <c>-wal</c> and
/// <c>-shm</c> while a connection is open, and a write-ahead log left by a program that stopped can
/// hold changes the database file itself does not have yet. Removing the database and leaving its log
/// would hand the next open a log for a database that is gone, so a database with any of them beside
/// it is left alone, which costs disk space rather than anything else.</para>
/// </summary>
internal static class SqliteDatabaseSignature
{
    private static ReadOnlySpan<byte> Header => "SQLite format 3\0"u8;

    /// <summary>The suffixes SQLite gives the files it keeps beside an open database.</summary>
    private static readonly string[] CompanionSuffixes = ["-journal", "-wal", "-shm"];

    /// <summary>The names SQLite gives the files it keeps beside a database called <paramref name="name"/> while it is open.</summary>
    public static IEnumerable<string> CompanionNamesOf(string name) => CompanionSuffixes.Select(suffix => name + suffix);

    /// <summary>
    /// Whether <paramref name="path"/> opens with the SQLite header and has no journal beside it. A
    /// file that cannot be read is not confirmed.
    /// </summary>
    /// <param name="listedBeside">
    /// Whether a whole listing of the folder holding <paramref name="path"/> found an entry of the name
    /// given. The caller has listed that folder to find the database, so a companion is not described
    /// again one by one, and a folder that could not be listed in full is never examined.
    /// </param>
    public static bool IsIdleDatabase(string path, Func<string, bool> listedBeside)
    {
        if (CompanionNamesOf(Path.GetFileName(path)).Any(listedBeside))
        {
            return false;
        }

        Span<byte> start = stackalloc byte[16];
        int read;

        try
        {
            using var stream = new FileStream(
                LongPath.Extended(path),
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            read = stream.ReadAtLeast(start, start.Length, throwOnEndOfStream: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Gone, refused, or held without sharing. None of them is a database this can vouch for.
            return false;
        }

        return start[..read].SequenceEqual(Header);
    }
}
