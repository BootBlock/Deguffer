using System.Numerics;
using System.Runtime.CompilerServices;

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
/// <see cref="Frozen"/> be read from any thread, as <see cref="AppendOnlyChunks{T}"/> allows for the
/// same reason.</para>
///
/// <para><b>Safe to call from several threads at once.</b> Every parse thread of a file-table read
/// interns a name for each record, about three million on a system volume, so the pool cannot be a
/// lock its callers take around each call. A name already held is found without a lock: its hash is
/// computed by the caller's thread, and the table of numbers is read as published. Only a name not
/// met before takes the lock, to be copied in and numbered, and a table that has to grow is rebuilt
/// from the hashes it stores, without reading any name back. Each distinct name still gets exactly
/// one number, because a name is numbered only under the lock and only after looking again there.
/// <see cref="ExploreTree.CompareByName(int[], Frozen, int, int)"/> relies on that: two equal
/// numbers are taken as one name without comparing them.</para>
/// </summary>
internal sealed class NamePool
{
    private const int ChunkShift = 15;
    private const int ChunkLength = 1 << ChunkShift;
    private const int ChunkMask = ChunkLength - 1;

    private const int InitialSlots = 64;

    /// <summary>The number of the empty name, which is what a slot no reader filled names.</summary>
    public const int Empty = 0;

    private readonly AppendOnlyChunks<Entry> _entries = new();
    private readonly Lock _gate = new();

    // Each slot is empty (zero) or a name's hash in the high half and its number plus one in the low
    // half, so a probe rejects almost every other name by its hash without reading its characters,
    // and the table grows by rehashing the slots alone. Published with a release write once the
    // name's characters and entry are in place, and read with an acquire read: a number found here
    // is one whose name can already be read.
    private long[] _slots = new long[InitialSlots];

    private char[][] _chunks = [];
    private int _chunkCount;
    private int _used;
    private long _textLength;

    public NamePool()
    {
        Intern([]);
    }

    /// <summary>How many distinct names are held.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>The number of <paramref name="name"/>, which is added where it is new.</summary>
    public int Intern(ReadOnlySpan<char> name)
    {
        var hash = string.GetHashCode(name);

        if (Find(Volatile.Read(ref _slots), name, hash, out var number, out _))
        {
            return number;
        }

        lock (_gate)
        {
            // Looked for again: another thread may have numbered the name since, and numbering it
            // twice would give one name two numbers.
            var slots = _slots;
            if (Find(slots, name, hash, out number, out var free))
            {
                return number;
            }

            number = Append(name);

            // Kept at most three quarters full, so a lookup that misses ends at an empty slot after
            // a short probe. A probe compares stored hashes, adjacent in memory, and reads a name
            // only where one matches, so a fuller table costs little and holds three million names
            // in 32 MB.
            if (_entries.Count * 4L > slots.Length * 3L)
            {
                slots = Grown(slots);
                free = FreeSlot(slots, hash);

                // Before the new name's slot is written, so a caller probing the old table misses,
                // takes the lock and finds the name in this one.
                Volatile.Write(ref _slots, slots);
            }

            Volatile.Write(ref slots[free], Slot(hash, number));
            return number;
        }
    }

    /// <summary>The names held so far, to be read from any thread.</summary>
    public Frozen Freeze()
    {
        lock (_gate)
        {
            return new Frozen(_chunks, _entries.Freeze(), _textLength);
        }
    }

    /// <summary>
    /// The names <paramref name="nameNumbers"/> use: a pool holding only those, each copied once, with
    /// every number in <paramref name="nameNumbers"/> rewritten to its name's number there, or
    /// <paramref name="names"/> itself, with the numbers left as they are, where the names left out
    /// hold no more than the names kept.
    ///
    /// <para>For a tree that keeps fewer nodes than its reader numbered names for. The file table's
    /// reader names every record on the volume, so a tree of one folder would otherwise keep every
    /// name on the disk to show the few under it. A tree of the whole volume uses all but a handful,
    /// and copying 1.4 million names to drop one cost 180 to 420 ms and 98 MiB on a system volume, so
    /// a copy is made only where the names left out hold more than the names kept. What a tree
    /// keeps beyond the names it uses is then never more than those names hold.</para>
    ///
    /// <para>No name is looked up: each was distinct in <paramref name="names"/>, so each stays
    /// distinct. They are copied in the order <paramref name="names"/> numbers them, so a kept name's
    /// new number is the count of kept names before it. A node's number is rewritten from that
    /// count, kept for each 64 names, so no map the size of <paramref name="names"/> is made, and only
    /// the names kept are read. The copy is handed back frozen because a pool that could intern more
    /// would hold nothing to find these by.</para>
    /// </summary>
    public static Frozen Keeping(Frozen names, Span<int> nameNumbers)
    {
        // One bit a name, so that marking a whole volume's names costs under 200 KB, and a pool
        // handed back as it was costs nothing more.
        var used = new ulong[(names.Count + 63) >> 6];

        foreach (var number in nameNumbers)
        {
            used[number >> 6] |= 1UL << number;
        }

        // The empty name is number zero in both pools, whether a node uses it or not.
        used[0] |= 1;

        var keptCount = 0;
        long keptLength = 0;

        for (var word = 0; word < used.Length; word++)
        {
            for (var bits = used[word]; bits != 0; bits &= bits - 1)
            {
                keptLength += names[(word << 6) | BitOperations.TrailingZeroCount(bits)].Length;
                keptCount++;
            }
        }

        var keptBytes = HeldBytes(keptLength, keptCount);
        if (HeldBytes(names.TextLength, names.Count) - keptBytes <= keptBytes)
        {
            return names;
        }

        var kept = new NamePool();
        var keptBefore = new int[used.Length];
        var counted = 0;

        for (var word = 0; word < used.Length; word++)
        {
            keptBefore[word] = counted;

            for (var bits = used[word]; bits != 0; bits &= bits - 1)
            {
                var number = (word << 6) | BitOperations.TrailingZeroCount(bits);

                if (number != Empty)
                {
                    kept.Append(names[number]);
                }

                counted++;
            }
        }

        foreach (ref var number in nameNumbers)
        {
            var word = number >> 6;
            number = keptBefore[word] + BitOperations.PopCount(used[word] & ((1UL << number) - 1));
        }

        return kept.Freeze();
    }

    /// <summary>What a pool holds for <paramref name="count"/> names of <paramref name="textLength"/> characters in all: their characters and their entries.</summary>
    private static long HeldBytes(long textLength, int count) =>
        (textLength * sizeof(char)) + ((long)count * Unsafe.SizeOf<Entry>());

    private static long Slot(int hash, int number) => ((long)hash << 32) | (uint)(number + 1);

    private static int HashOf(long slot) => (int)(slot >> 32);

    private static int NumberOf(long slot) => (int)(uint)slot - 1;

    private static ReadOnlySpan<char> Read(char[][] chunks, in Entry entry) =>
        chunks[entry.Location >> ChunkShift].AsSpan(entry.Location & ChunkMask, entry.Length);

    /// <summary>
    /// Whether <paramref name="slots"/> holds <paramref name="name"/>, and where not, the empty slot
    /// its probe ended at.
    ///
    /// <para>Called without the lock as well as under it. A slot is read with an acquire read, and the
    /// chunks and entries only after it, so they are at least as new as the name the slot numbers.
    /// </para>
    /// </summary>
    private bool Find(long[] slots, ReadOnlySpan<char> name, int hash, out int number, out int free)
    {
        var mask = slots.Length - 1;

        for (var i = hash & mask; ; i = (i + 1) & mask)
        {
            var slot = Volatile.Read(ref slots[i]);

            if (slot == 0)
            {
                number = default;
                free = i;
                return false;
            }

            if (HashOf(slot) == hash)
            {
                var candidate = NumberOf(slot);
                if (name.SequenceEqual(Read(_chunks, _entries[candidate])))
                {
                    number = candidate;
                    free = default;
                    return true;
                }
            }
        }
    }

    /// <summary>A table twice the size holding every slot of <paramref name="slots"/>, placed by its stored hash.</summary>
    private static long[] Grown(long[] slots)
    {
        var grown = new long[slots.Length * 2];

        foreach (var slot in slots)
        {
            if (slot != 0)
            {
                grown[FreeSlot(grown, HashOf(slot))] = slot;
            }
        }

        return grown;
    }

    /// <summary>The empty slot a probe for <paramref name="hash"/> ends at, in a table only the lock's holder writes.</summary>
    private static int FreeSlot(long[] slots, int hash)
    {
        var mask = slots.Length - 1;
        var i = hash & mask;

        while (slots[i] != 0)
        {
            i = (i + 1) & mask;
        }

        return i;
    }

    /// <summary>Copy <paramref name="name"/> in, and number it. Called under the lock, or on a pool no other thread can see.</summary>
    private int Append(ReadOnlySpan<char> name)
    {
        if (_chunkCount == 0 || name.Length > ChunkLength - _used)
        {
            if (_chunkCount == _chunks.Length)
            {
                // A new directory array rather than one resized in place: a Frozen already handed
                // out, or a lookup under way without the lock, keeps the old one, which still names
                // every chunk it covers.
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
        _textLength += name.Length;

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

        internal Frozen(char[][] chunks, AppendOnlyChunks<Entry>.Prefix entries, long textLength)
        {
            _chunks = chunks;
            _entries = entries;
            TextLength = textLength;
        }

        public int Count => _entries.Count;

        /// <summary>How many characters the names hold in all.</summary>
        public long TextLength { get; }

        public ReadOnlySpan<char> this[int number] => Read(_chunks, in _entries[number]);
    }
}
