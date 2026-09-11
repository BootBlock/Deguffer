using System.Collections.Concurrent;
using System.Text.Json;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// Where the last clean of each location found Windows refusing, kept across runs so that a later
/// preview can ask again about exactly those places and leave out whatever is still refused.
///
/// <para><b>Remembered, rather than asked of every file on every preview.</b> Only an open for
/// deletion reaches whatever refuses (see <see cref="DeletionProbe"/>), and while that open lasts it
/// stops another program opening the same file without sharing delete access. Asking it of every
/// file Deguffer measures would do that across millions of files on every preview, and would take
/// every location Deguffer deletes itself off §5.5's fast path, because the file table cannot ask
/// it. The clean already asks the real question of every file it attempts, so the preview asks
/// again only where the clean was refused. There the answer is nearly always "still refused", and a
/// refused open leaves no handle to get in anybody's way.</para>
///
/// <para><b>It records where to look, never what the answer was.</b> Every preview asks again, so a
/// refusal that has since lifted is counted at once, without waiting for a clean that the row would
/// no longer offer. What it cannot see is a place never cleaned over: the first preview on a machine
/// offers what the clean then reports it could not take, and every preview after that one does
/// not.</para>
///
/// <para><b>One instance per file, by construction.</b> Every provider records into the same file,
/// and two instances over it would each save their own view and discard the other's.
/// <see cref="For"/> is how product code reaches one.</para>
/// </summary>
public sealed class RefusalRecord
{
    private static readonly ConcurrentDictionary<string, RefusalRecord> ByFile =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly Dictionary<string, string[]> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly string _file;

    /// <summary>
    /// A second instance over the same file, for a test standing in for the next run of the app.
    /// Product code goes through <see cref="For"/>, for the reason the summary gives.
    /// </summary>
    internal RefusalRecord(IUserEnvironment environment)
        : this(FileFor(environment))
    {
    }

    private RefusalRecord(string file)
    {
        _file = file;
        Load();
    }

    /// <summary>
    /// The record kept under <paramref name="environment"/>'s local application data. Keyed by the
    /// file rather than by the environment, so every provider constructed against one profile shares
    /// one — which is what the app does — while a test's invented profile keeps its own.
    /// </summary>
    public static RefusalRecord For(IUserEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return ByFile.GetOrAdd(FileFor(environment), file => new RefusalRecord(file));
    }

    /// <summary>
    /// The places the last clean of <paramref name="stepPath"/> found Windows refusing, in display
    /// form, or none.
    /// </summary>
    public IReadOnlyList<string> At(string stepPath)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(Key(stepPath), out var places) ? places : [];
        }
    }

    /// <summary>
    /// What a clean of <paramref name="stepPath"/> has just found, replacing whatever an earlier one
    /// did. An empty list clears the entry: the clean asked every file it attempted, and none refused.
    /// </summary>
    public void Replace(string stepPath, IReadOnlyList<string> refusedAt)
    {
        ArgumentNullException.ThrowIfNull(refusedAt);

        var key = Key(stepPath);

        lock (_gate)
        {
            if (refusedAt.Count == 0)
            {
                if (!_entries.Remove(key))
                {
                    return;
                }
            }
            else
            {
                // The ordinary case after the first clean is the same refusals again, and rewriting
                // the file to say so is I/O at the end of every step for nothing.
                if (_entries.TryGetValue(key, out var existing)
                    && existing.SequenceEqual(refusedAt, StringComparer.OrdinalIgnoreCase))
                {
                    return;
                }

                _entries[key] = [.. refusedAt];
            }

            Save();
        }
    }

    private static string FileFor(IUserEnvironment environment) =>
        Path.Combine(environment.LocalAppData, "Deguffer", "refusals.json");

    /// <summary>
    /// A step's path in one spelling. The executor records against the path it removed and the
    /// preview asks with the path it planned, and those are the same string today — but a trailing
    /// separator or §6.3's prefix on either side would make the record silently miss, and the row
    /// would go back to offering what Windows refuses.
    /// </summary>
    private static string Key(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(LongPath.Display(path)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private void Load()
    {
        try
        {
            if (!LongPath.FileExists(_file))
            {
                return;
            }

            var loaded = JsonSerializer.Deserialize<Dictionary<string, string[]>>(
                File.ReadAllText(LongPath.Extended(_file)), SerializerOptions);

            foreach (var (path, places) in loaded ?? [])
            {
                // A location that has gone since is never planned again, so nothing would ever clear
                // its entry. Dropped on the way in, so the file cannot accumulate every project a
                // machine has had.
                if (LongPath.DirectoryExists(path) || LongPath.FileExists(path))
                {
                    _entries[Key(path)] = places;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A record that cannot be read is an empty one. The cost is one preview that offers what
            // Windows refuses, and the next clean writes the record again.
        }
    }

    private void Save()
    {
        try
        {
            if (Path.GetDirectoryName(_file) is { } directory)
            {
                Directory.CreateDirectory(LongPath.Extended(directory));
            }

            File.WriteAllText(LongPath.Extended(_file), JsonSerializer.Serialize(_entries, SerializerOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The same cost as a record that cannot be read, and the same recovery.
        }
    }
}
