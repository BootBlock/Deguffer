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
    public static ChecksumKey Of(FileState file, ContentPart part, ChecksumAlgorithm algorithm) => new(
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
/// life of the app, loaded the first time a search asks.
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
/// <para><b>Written behind the search, and only for a new value.</b> A full store is tens of
/// megabytes, so a search hands its result back without waiting for the write
/// (<see cref="StoreInBackground"/>): the values are copied before that returns, and each write runs
/// on its copy, one after another. The next search waits for a write still running before it reads
/// (<see cref="WhenStoredAsync"/>), and so does the app before it closes or relaunches
/// (<see cref="Save"/>). A value only used on a new day is no reason to write the whole store again:
/// the day goes with the next write that has a new value, or with the close, and a day a few
/// searches stale is still well inside <see cref="DaysKeptUnused"/>.</para>
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
    private readonly Action<string, Action<Stream>> _store;
    private readonly Lazy<ConcurrentDictionary<ChecksumKey, RememberedChecksum>> _remembered;

    /// <summary>Taken to copy the values for a write and to chain that write after the last.</summary>
    private readonly Lock _gate = new();

    /// <summary>Whether a value was remembered since the values were last copied for a write.</summary>
    private int _newValue;

    /// <summary>Whether a value was used on a new day since the values were last copied for a write.</summary>
    private int _newDay;

    /// <summary>The last write started, which answers whether the store was written. Under <see cref="_gate"/>.</summary>
    private Task<bool> _writing = Task.FromResult(true);

    public ChecksumCache(IUserEnvironment environment, TimeProvider time)
        : this(FileOf(environment), time)
    {
    }

    /// <param name="file">Where the store is, so a test can damage it or read it.</param>
    /// <param name="mostKept">The most values kept, so a test can pass the bound without half a million files.</param>
    /// <param name="store">Writes the store's file, so a test can count writes, or hold one to see what waits for it.</param>
    internal ChecksumCache(string file, TimeProvider time, int mostKept = MostKept, Action<string, Action<Stream>>? store = null)
    {
        _file = file;
        _time = time;
        _mostKept = mostKept;
        _store = store ?? StoreFile.Replace;
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
    internal ContentChecksum? Find(FileState file, ContentPart part, ChecksumAlgorithm algorithm)
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
            Volatile.Write(ref _newDay, 1);
        }

        return found.Value;
    }

    /// <summary>
    /// Remember <paramref name="value"/> as the checksum of <paramref name="part"/> of the file as
    /// <paramref name="file"/> described it before its content was read.
    /// </summary>
    internal void Remember(FileState file, ContentPart part, ContentChecksum value)
    {
        _remembered.Value[ChecksumKey.Of(file, part, value.Algorithm)] = new RememberedChecksum(value, Today);
        Volatile.Write(ref _newValue, 1);
    }

    /// <summary>
    /// Start writing the store where a value was remembered since it was last written, and return
    /// without waiting for the write. Called once a search has stopped reading: the values are copied
    /// before this returns, so the next search cannot change what the write is writing.
    /// </summary>
    public void StoreInBackground() => StartWrite(newDayIsReason: false);

    /// <summary>
    /// Completes once the write in progress, if any, has ended, so a search reads only once the last
    /// search's values are on disk and no write runs beside its reads.
    /// </summary>
    public Task WhenStoredAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return _writing.WaitAsync(ct);
        }
    }

    /// <summary>
    /// Write the store, after any write in progress, where anything was remembered or used on a new
    /// day since it was last written, and wait for it. Called as the app closes or relaunches.
    /// Returns false where the store could not be written, which costs the next search only the reads
    /// it would have saved.
    /// </summary>
    public bool Save() => StartWrite(newDayIsReason: true).GetAwaiter().GetResult();

    /// <summary>
    /// Copy the values and chain their write after the last, where there is reason to write. Returns
    /// the write started, or the last one where none was.
    /// </summary>
    private Task<bool> StartWrite(bool newDayIsReason)
    {
        lock (_gate)
        {
            if (!_remembered.IsValueCreated
                || (Volatile.Read(ref _newValue) == 0 && (!newDayIsReason || Volatile.Read(ref _newDay) == 0)))
            {
                return _writing;
            }

            // Cleared before the copy, so a value remembered while the copy is taken marks the cache
            // changed again rather than going unwritten.
            var reasons = new WriteReasons(Interlocked.Exchange(ref _newValue, 0) != 0, Interlocked.Exchange(ref _newDay, 0) != 0);
            var copy = _remembered.Value.ToArray();
            var today = Today;
            var previous = _writing;

            _writing = Task.Run(async () =>
            {
                // Only for its end: a write that failed put its reasons back, so this copy covers it.
                await previous.ConfigureAwait(false);

                return Write(copy, today, reasons);
            });

            return _writing;
        }
    }

    /// <summary>
    /// Write <paramref name="copy"/> less what has gone unused too long or is past
    /// <see cref="MostKept"/>, and drop the same values from the cache.
    /// </summary>
    private bool Write(KeyValuePair<ChecksumKey, RememberedChecksum>[] copy, int today, WriteReasons reasons)
    {
        var oldest = today - DaysKeptUnused;
        var kept = Array.FindAll(copy, entry => entry.Value.LastUsedDay >= oldest);

        // Sorting is most of a full store's work, and decides nothing until more are left than the
        // bound keeps.
        if (kept.Length > _mostKept)
        {
            kept = [.. kept.OrderByDescending(entry => entry.Value.LastUsedDay).Take(_mostKept)];
        }

        if (kept.Length < copy.Length)
        {
            var keep = kept.Select(entry => entry.Key).ToHashSet();
            var remembered = _remembered.Value;

            foreach (var entry in copy)
            {
                if (!keep.Contains(entry.Key))
                {
                    // Only as it was copied: a value remembered again or used since the copy stays.
                    remembered.TryRemove(entry);
                }
            }
        }

        try
        {
            _store(_file, stream => ChecksumStoreFormat.Write(stream, kept));

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A full disk, a folder refused, or another copy of Deguffer writing the store. The
            // reasons to write stand, so the next write tries again.
            if (reasons.NewValue)
            {
                Volatile.Write(ref _newValue, 1);
            }

            if (reasons.NewDay)
            {
                Volatile.Write(ref _newDay, 1);
            }

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

    /// <summary>What a write was started for, put back where the write fails.</summary>
    private readonly record struct WriteReasons(bool NewValue, bool NewDay);
}
