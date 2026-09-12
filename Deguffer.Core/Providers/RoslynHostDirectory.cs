using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// One program's set of Roslyn solution indexes under <c>Roslyn\Cache</c>, recognised by the whole of the
/// layout Roslyn writes rather than by its name.
///
/// <para><b>The layout.</b> Roslyn's own source (<c>DefaultPersistentStorageConfiguration</c> and
/// <c>SQLitePersistentStorageService</c>) puts each solution's database at
/// <c>Cache\&lt;program&gt;\&lt;solution&gt;\sqlite3\v2\storage.ide</c>, with a <c>db.lock</c> beside it and
/// SQLite's <c>-wal</c> and <c>-shm</c> files. Both directory names are the first twenty characters of a file
/// name — the hosting program's, then the solution's — a dash, and a Base64 checksum of that file's full
/// path with <c>/</c> removed. Microsoft documents none of it, so a directory that departs from it anywhere is
/// Tier 4 rather than a guess.</para>
///
/// <para><b>By contents as well as by name</b>, on <see cref="ContentSignature"/>'s reasoning: the name has
/// Roslyn's shape, but anybody can make a directory with that shape, so what vouches for one is that
/// everything inside it is a solution index. The match is total, so one unexpected entry anywhere in the
/// tree disqualifies the whole set, and it reads directory entries only. <see cref="ContentSignature"/>
/// itself cannot express it: that recognises one flat directory, and this is a fixed tree three levels
/// deep.</para>
///
/// <para><b>The age comes from the same reading.</b> SQLite writes the database files in place, three levels
/// below the directory the user chooses, and that moves none of the directories above them. So
/// <see cref="DirectoryAge"/>'s one-level answer would date an index written this morning by the day its
/// solution was first opened, which is the direction that invites a deletion. The recognition has already
/// listed every entry in the tree, so the newest of them is exact and costs nothing further (G5).</para>
/// </summary>
/// <param name="Program">
/// The first part of the directory's name: the hosting program's file name, which Roslyn cuts to twenty
/// characters — <c>devenv.exe</c>, <c>ServiceHub.RoslynCod</c>.
/// </param>
/// <param name="Solutions">How many solutions' indexes the set holds.</param>
/// <param name="LastWritten">The newest last-write time of any entry in the set, the set's own included.</param>
public sealed partial record RoslynHostDirectory(string Program, int Solutions, DateTime LastWritten)
{
    private const string DatabaseFile = "storage.ide";

    /// <summary>
    /// The database and what Roslyn keeps beside it. No other file is recognised.
    ///
    /// <para>Matched in exactly this case, as <c>sqlite3</c> and <c>v2</c> are, because Roslyn writes every one
    /// of these names from a constant. Another casing is something Roslyn did not write, and a directory
    /// with per-directory case sensitivity turned on can hold <c>v2</c> and <c>V2</c> side by side, which an
    /// ignore-case match would read as one.</para>
    /// </summary>
    private static readonly HashSet<string> IndexFiles = new(StringComparer.Ordinal)
    {
        DatabaseFile,
        "storage.ide-wal",
        "storage.ide-shm",
        "db.lock",
    };

    /// <summary>
    /// A name Roslyn builds: one to twenty characters, a dash, and a Base64 checksum. The checksum was
    /// observed as twenty-four characters and, in sets written by older builds, twenty-eight — fewer where a
    /// <c>/</c> was removed — and it never holds a dash, which is what lets a program name that does hold one
    /// still match.
    ///
    /// Anchored with <c>\A</c> and <c>\z</c> rather than <c>^</c> and <c>$</c>: <c>$</c> also matches before a
    /// trailing newline, and a check that decides whether a directory may be deleted should admit no such
    /// reading.
    /// </summary>
    [GeneratedRegex(@"\A(?<program>.{1,20})-[A-Za-z0-9+=]{16,28}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RoslynName();

    /// <summary>
    /// The set at <paramref name="directory"/>, or null where it is not one: a name Roslyn did not build,
    /// anything inside that is not a solution index, a link anywhere in the tree, a set with no index in it,
    /// or a directory that could not be read.
    /// </summary>
    public static RoslynHostDirectory? Read(string directory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var host = new DirectoryInfo(LongPath.Extended(directory));

        if (RoslynName().Match(host.Name) is not { Success: true } name)
        {
            return null;
        }

        try
        {
            if (!host.Exists || IsLink(host))
            {
                return null;
            }

            var newest = host.LastWriteTimeUtc;
            var solutions = 0;

            foreach (var entry in host.EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();

                if (entry is not DirectoryInfo solution
                    || IsLink(solution)
                    || !RoslynName().IsMatch(solution.Name)
                    || ReadSolution(solution, ct) is not { } written)
                {
                    return null;
                }

                newest = Later(newest, written);
                solutions++;
            }

            // An empty set is no evidence either way, and "no evidence" is not the same as "recognised".
            return solutions == 0
                ? null
                : new RoslynHostDirectory(name.Groups["program"].Value, solutions, newest);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // Cannot see inside it, so cannot vouch for it.
            return null;
        }
    }

    /// <summary>
    /// One solution's index: <c>sqlite3\v2</c> and nothing else on the way down, and at the bottom the
    /// database with only its own companions. The newest last-write time in it, or null where it is not one.
    /// </summary>
    private static DateTime? ReadSolution(DirectoryInfo solution, CancellationToken ct)
    {
        if (SoleDirectory(solution, "sqlite3") is not { } engine
            || SoleDirectory(engine, "v2") is not { } version)
        {
            return null;
        }

        var newest = Later(Later(solution.LastWriteTimeUtc, engine.LastWriteTimeUtc), version.LastWriteTimeUtc);
        var sawDatabase = false;

        foreach (var entry in version.EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();

            if (entry is not FileInfo || IsLink(entry) || !IndexFiles.Contains(entry.Name))
            {
                return null;
            }

            // The lock and the SQLite companions exist around a database, never instead of one, so a
            // directory holding only those has no index in it to vouch for.
            sawDatabase |= entry.Name.Equals(DatabaseFile, StringComparison.Ordinal);
            newest = Later(newest, entry.LastWriteTimeUtc);
        }

        return sawDatabase ? newest : null;
    }

    /// <summary>
    /// The only entry in <paramref name="parent"/>, where it is a real directory called exactly
    /// <paramref name="name"/>; otherwise null. Every entry has to pass, and no directory holds two entries
    /// with one exact name, so passing means there was one.
    /// </summary>
    private static DirectoryInfo? SoleDirectory(DirectoryInfo parent, string name)
    {
        DirectoryInfo? found = null;

        foreach (var entry in parent.EnumerateFileSystemInfos())
        {
            if (entry is not DirectoryInfo directory
                || IsLink(directory)
                || !directory.Name.Equals(name, StringComparison.Ordinal))
            {
                return null;
            }

            found = directory;
        }

        return found;
    }

    private static bool IsLink(FileSystemInfo entry) => entry.Attributes.HasFlag(FileAttributes.ReparsePoint);

    private static DateTime Later(DateTime first, DateTime second) => first > second ? first : second;
}
