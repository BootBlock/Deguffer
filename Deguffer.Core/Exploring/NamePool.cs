namespace Deguffer.Core.Exploring;

/// <summary>
/// Every name an Explore tree holds, each distinct name once, as characters in a few large buffers
/// and a number to find each by.
///
/// <para>What a node holds instead of a string. A string is an object of its own, about 20 bytes of
/// header before two bytes a character, so a volume of five million names was five million objects
/// and some 300 MB, and every full collection traced each of them. A name repeats heavily on a
/// developer's volume besides — <c>index.js</c>, <c>package.json</c>, <c>LICENSE</c>, and a
/// <c>node_modules</c> in every package — so a name met again costs its node four bytes and nothing
/// more.</para>
///
/// <para>Matched exactly, case included, because a name is kept as the disk holds it: a directory
/// with case sensitivity enabled holds <c>Cache</c> and <c>cache</c> as two names.</para>
///
/// <para>The characters are kept in chunks of one fixed length, under the 85,000 bytes that put an
/// array on the Large Object Heap, and a chunk is never replaced once allocated. That is what lets a
/// <see cref="Frozen"/> taken under the caller's lock be read from any thread after it is released,
/// as <see cref="AppendOnlyChunks{T}"/> allows for the same reason.</para>
///
/// <para>Not safe for concurrent writes: the caller serialises <see cref="Intern"/>.</para>
/// </summary>
internal sealed class NamePool
{
    private const int ChunkShift = 15;
    private const int ChunkLength = 1 << ChunkShift;
    private const int ChunkMask = ChunkLength - 1;

    /// <summary>The number of the empty name, which is what a slot no reader filled names.</summary>
    public const int Empty = 0;

    private readonly AppendOnlyChunks<Entry> _entries = new();
    private readonly HashSet<int>.AlternateLookup<ReadOnlySpan<char>> _byName;

    private char[][] _chunks = [];
    private int _chunkCount;
    private int _used;

    public NamePool()
    {
        // Numbers in the set, compared by the names they stand for, so a name is looked up as the
        // span it arrives as and the set holds no string at all.
        _byName = new HashSet<int>(new ByName(this)).GetAlternateLookup<ReadOnlySpan<char>>();

        Intern([]);
    }

    /// <summary>How many distinct names are held.</summary>
    public int Count => _entries.Count;

    /// <summary>The number of <paramref name="name"/>, which is added where it is new.</summary>
    public int Intern(ReadOnlySpan<char> name)
    {
        if (!_byName.TryGetValue(name, out var number))
        {
            _byName.Add(name);
            number = _entries.Count - 1;
        }

        return number;
    }

    /// <summary>
    /// The names held so far, to be read from any thread once the caller's lock has published them.
    /// </summary>
    public Frozen Freeze() => new(_chunks, _entries.Freeze());

    /// <summary>
    /// A pool holding only the names <paramref name="nameNumbers"/> use, each copied once, with every
    /// number in <paramref name="nameNumbers"/> rewritten to its name's number there.
    ///
    /// <para>For a tree that keeps fewer nodes than its reader numbered names for. The file table's
    /// reader names every record on the volume, so a tree of one folder would otherwise keep every
    /// name on the disk to show the few under it. No name is looked up: each was distinct in
    /// <paramref name="names"/>, so each stays distinct, and the pool is handed back frozen because
    /// one that could intern more would hold nothing to find these by.</para>
    /// </summary>
    public static Frozen Keeping(Frozen names, Span<int> nameNumbers)
    {
        var kept = new NamePool();

        // Zero in the map is "not copied yet", which the empty name, number zero in both pools,
        // never needs to be.
        var renumbered = new int[names.Count];

        foreach (ref var number in nameNumbers)
        {
            if (number == Empty)
            {
                continue;
            }

            if (renumbered[number] == Empty)
            {
                renumbered[number] = kept.Append(names[number]);
            }

            number = renumbered[number];
        }

        return kept.Freeze();
    }

    private ReadOnlySpan<char> this[int number] => Read(_chunks, _entries[number]);

    private static ReadOnlySpan<char> Read(char[][] chunks, in Entry entry) =>
        chunks[entry.Location >> ChunkShift].AsSpan(entry.Location & ChunkMask, entry.Length);

    /// <summary>Copy <paramref name="name"/> in, and number it.</summary>
    private int Append(ReadOnlySpan<char> name)
    {
        if (_chunkCount == 0 || name.Length > ChunkLength - _used)
        {
            if (_chunkCount == _chunks.Length)
            {
                // A new directory array rather than one resized in place: a Frozen already handed
                // out keeps the old one, which still names every chunk it covers.
                var grown = new char[Math.Max(4, _chunks.Length * 2)][];
                _chunks.CopyTo(grown, 0);
                _chunks = grown;
            }

            // A name longer than a chunk is given one of its own. Only a scan's root, which carries
            // the path the user chose, could be: a name on the disk is at most 255 characters.
            _chunks[_chunkCount++] = new char[Math.Max(ChunkLength, name.Length)];
            _used = 0;
        }

        // Past this, a location no longer fits an int. That is four GiB of distinct names, which no
        // volume comes near, and failing beats numbering one name as another.
        var chunk = _chunkCount - 1;
        if (chunk > int.MaxValue >> ChunkShift)
        {
            throw new InvalidOperationException("The tree holds more name text than one pool can number.");
        }

        name.CopyTo(_chunks[chunk].AsSpan(_used));
        _entries.Add() = new Entry((chunk << ChunkShift) | _used, name.Length);

        // A chunk given to one long name is full with it.
        _used = name.Length > ChunkLength ? ChunkLength : _used + name.Length;

        return _entries.Count - 1;
    }

    /// <summary>Where one name's characters are: its chunk and offset in one int, and its length.</summary>
    internal readonly record struct Entry(int Location, int Length);

    /// <summary>The names numbered before <see cref="Freeze"/> was called, as they were then.</summary>
    internal readonly struct Frozen
    {
        private readonly char[][] _chunks;
        private readonly AppendOnlyChunks<Entry>.Prefix _entries;

        internal Frozen(char[][] chunks, AppendOnlyChunks<Entry>.Prefix entries)
        {
            _chunks = chunks;
            _entries = entries;
        }

        public int Count => _entries.Count;

        public ReadOnlySpan<char> this[int number] => Read(_chunks, in _entries[number]);
    }

    /// <summary>
    /// Compares numbers by the names they stand for, and looks a name up by its span. Two numbers
    /// are equal only where they are one number, because a name is numbered once.
    /// </summary>
    private sealed class ByName(NamePool pool) : IEqualityComparer<int>, IAlternateEqualityComparer<ReadOnlySpan<char>, int>
    {
        public bool Equals(int x, int y) => x == y;

        public int GetHashCode(int obj) => string.GetHashCode(pool[obj]);

        public bool Equals(ReadOnlySpan<char> alternate, int other) => alternate.SequenceEqual(pool[other]);

        public int GetHashCode(ReadOnlySpan<char> alternate) => string.GetHashCode(alternate);

        public int Create(ReadOnlySpan<char> alternate) => pool.Append(alternate);
    }
}
