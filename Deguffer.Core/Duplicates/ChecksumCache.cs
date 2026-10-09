using System.Collections.Concurrent;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Duplicates;

/// <summary>
/// What a remembered checksum is remembered under (§7.4): the file by its volume and number, and the
/// state it was read in, its length, last-modified time and change time, never its path. A file
/// changed in any of them is a new file to the cache.
/// </summary>
/// <param name="Part">What was read: the first and last blocks, or every byte.</param>
internal readonly record struct ChecksumKey(
    FileIdentity Identity,
    long Length,
    long ModifiedTicks,
    long ChangedTicks,
    ChecksumAlgorithm Algorithm,
    ContentPart Part)
{
    public static ChecksumKey Of(FileDescription file, ContentPart part, ChecksumAlgorithm algorithm) => new(
        file.Identity,
        file.Length,
        file.Modified.Ticks,
        file.Changed.Ticks,
        algorithm,
        part);
}

/// <summary>A remembered checksum, and the day it was last read or used, counted from 1 January 1970 (UTC).</summary>
internal readonly record struct RememberedChecksum(ContentChecksum Value, int LastUsedDay);

/// <summary>
/// The checksums duplicate searches read, kept from one search to the next so a second search reads
/// only what changed (§7.4), in a store under <c>%LOCALAPPDATA%\Deguffer</c>. One instance for the
/// life of the app, loaded the first time a search asks and stored when a search ends.
///
/// <para><b>Never a path.</b> A checksum is kept under the file's volume and number, its length and
/// its last-modified and change times (<see cref="ChecksumKey"/>), so the store names no file. A
/// rename moves a file's change time on NTFS, so a renamed file is read again. A volume whose
/// numbers do not stay with their files keeps nothing here (<see cref="Keeps"/>).</para>
///
/// <para><b>It groups, and never licenses a removal.</b> A remembered value stands in for reading a
/// file's bytes, never for opening them: it is used only once the content is open and the file is
/// judged as a read would judge it, with every part of the key unchanged, and a removal reads the
/// bytes again whatever any checksum said.</para>
///
/// <para><b>A store that cannot be read loads empty.</b> Missing, refused, torn, hand-edited or of a
/// format this build does not know, it costs one search the reads it would have saved, and a store
/// read wrongly would group files that differ or part files that match.</para>
///
/// <para><b>Bounded.</b> A value no search has used for <see cref="DaysKeptUnused"/> days is
/// dropped, since the file it was read from is most likely gone, and past <see cref="MostKept"/>
/// values the ones used longest ago go first.</para>
/// </summary>
public sealed class ChecksumCache
{
    /// <summary>
    /// The most values kept. Each takes under 120 bytes in the store, so the store stays under 60 MB,
    /// and half a million covers the files read by searches of several large drives.
    /// </summary>
    public const int MostKept = 500_000;

    /// <summary>How long a value no search used is kept: long enough for a search run twice a year.</summary>
    public const int DaysKeptUnused = 180;

    private readonly string _file;
    private readonly TimeProvider _time;
    private readonly int _mostKept;
    private readonly Lazy<ConcurrentDictionary<ChecksumKey, RememberedChecksum>> _remembered;

    /// <summary>Whether anything was remembered, or used on a new day, since the store was last written.</summary>
    private int _changed;

    public ChecksumCache(IUserEnvironment environment, TimeProvider time)
        : this(FileOf(environment), time)
    {
    }

    /// <param name="file">Where the store is, so a test can damage it or read it.</param>
    /// <param name="mostKept">The most values kept, so a test can pass the bound without half a million files.</param>
    internal ChecksumCache(string file, TimeProvider time, int mostKept = MostKept)
    {
        _file = file;
        _time = time;
        _mostKept = mostKept;
        _remembered = new(Load);
    }

    /// <summary>The store's file for the signed-in account.</summary>
    internal static string FileOf(IUserEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return Path.Combine(environment.LocalAppData, "Deguffer", "checksums.bin");
    }

    /// <summary>
    /// Whether a checksum of <paramref name="file"/> may be remembered and used: only where its
    /// volume keeps a file's number with the file (<see cref="LocalVolume.KeepsFileNumbers"/>), which
    /// FAT and exFAT do not, and where it was identified by <c>FileIdInfo</c>, whose serial number is
    /// the volume's whole one.
    /// </summary>
    internal static bool Keeps(DuplicateCandidate file) =>
        file.Route is IdentityRoute.FileId && file.Volume.KeepsFileNumbers;

    private int Today => (int)(_time.GetUtcNow().UtcDateTime - DateTime.UnixEpoch).TotalDays;

    /// <summary>
    /// The checksum of <paramref name="part"/> of the file as <paramref name="file"/> describes it
    /// now, by <paramref name="algorithm"/>, or null where none is remembered.
    /// </summary>
    internal ContentChecksum? Find(FileDescription file, ContentPart part, ChecksumAlgorithm algorithm)
    {
        var key = ChecksumKey.Of(file, part, algorithm);
        var remembered = _remembered.Value;

        if (!remembered.TryGetValue(key, out var found))
        {
            return null;
        }

        var today = Today;

        if (found.LastUsedDay != today)
        {
            remembered[key] = found with { LastUsedDay = today };
            Volatile.Write(ref _changed, 1);
        }

        return found.Value;
    }

    /// <summary>
    /// Remember <paramref name="value"/> as the checksum of <paramref name="part"/> of the file as
    /// <paramref name="file"/> described it before its content was read.
    /// </summary>
    internal void Remember(FileDescription file, ContentPart part, ContentChecksum value)
    {
        _remembered.Value[ChecksumKey.Of(file, part, value.Algorithm)] = new RememberedChecksum(value, Today);
        Volatile.Write(ref _changed, 1);
    }

    /// <summary>
    /// Write what is remembered to the store, less what has gone unused too long or is past
    /// <see cref="MostKept"/>, where anything changed since it was last written. Called once a search
    /// has stopped reading. Returns false where the store could not be written, which costs the next
    /// search only the reads it would have saved.
    /// </summary>
    public bool Save()
    {
        if (!_remembered.IsValueCreated || Interlocked.Exchange(ref _changed, 0) == 0)
        {
            return true;
        }

        var remembered = _remembered.Value;
        var oldest = Today - DaysKeptUnused;
        var kept = remembered.ToArray()
            .Where(entry => entry.Value.LastUsedDay >= oldest)
            .OrderByDescending(entry => entry.Value.LastUsedDay)
            .Take(_mostKept)
            .ToArray();

        if (kept.Length < remembered.Count)
        {
            var keep = kept.Select(entry => entry.Key).ToHashSet();

            foreach (var key in remembered.Keys.Where(key => !keep.Contains(key)))
            {
                remembered.TryRemove(key, out _);
            }
        }

        var written = _file + ".tmp";

        try
        {
            Directory.CreateDirectory(LongPath.Extended(Path.GetDirectoryName(_file)!));

            using (var stream = new FileStream(LongPath.Extended(written), FileMode.Create, FileAccess.Write, FileShare.None))
            {
                ChecksumStoreFormat.Write(stream, kept);
            }

            // Written beside and moved over, so an interrupted save leaves the last whole store, never
            // a torn one.
            File.Move(LongPath.Extended(written), LongPath.Extended(_file), overwrite: true);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A full disk, a folder refused, or another copy of Deguffer writing the store. Kept as
            // changed, so the next search tries again.
            Volatile.Write(ref _changed, 1);
            TryDelete(written);

            return false;
        }
    }

    private ConcurrentDictionary<ChecksumKey, RememberedChecksum> Load()
    {
        try
        {
            using var stream = new FileStream(LongPath.Extended(_file), FileMode.Open, FileAccess.Read, FileShare.Read);

            if (stream.Length > ChecksumStoreFormat.LongestStore)
            {
                return new();
            }

            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);

            return new(ChecksumStoreFormat.Read(bytes) ?? []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not written yet, refused, or cut short as it was read: nothing remembered.
            return new();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(LongPath.Extended(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next save to write over: it is this store's own name, never read.
        }
    }
}
