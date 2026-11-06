namespace Deguffer.Core.Scanning;

/// <summary>
/// The characters of the names a listing lends rather than builds as strings
/// (<see cref="FileNames.AllLent"/>), kept in one worker's memory and reused for every directory it
/// lists.
///
/// <para>Chunks of one fixed length, under the 85,000 bytes that put an array on the Large Object
/// Heap. The first few are kept for the whole walk, which is room for the names of almost every
/// directory, so lending them allocates nothing. A directory with more names takes more chunks, and
/// they are let go once it is over: kept, the largest directory each worker met would stay in memory
/// until the walk ended, on every worker.</para>
///
/// <para>Not thread-safe: one worker uses it at a time, as it does the rest of its
/// <see cref="ListingMemory"/>.</para>
/// </summary>
internal sealed class ListingNames
{
    // 64 KiB of characters, which holds the longest name any listing can give: Windows counts a
    // name's length in bytes in 16 bits.
    private const int ChunkLength = 32 * 1024;

    // About 20,000 names of a usual length.
    private const int KeptChunks = 8;

    private readonly List<char[]> _chunks = [];

    // Where the next name goes, counted from the start of the first chunk, so a loan's place is one
    // number: its chunk is that number over the chunk length.
    private int _used;

    // What every name lent since the last recall was lent through. Made at the first loan rather than
    // at the recall, because every walk recalls once for each directory and most never lend.
    private Lending? _lending;

    /// <summary>
    /// End every loan made so far, so that the memory can be lent again. A name lent before this
    /// throws when it is read, rather than reading as whatever was lent at its place since.
    /// </summary>
    public void Recall()
    {
        _lending?.End();
        _lending = null;
        _used = 0;

        if (_chunks.Count > KeptChunks)
        {
            _chunks.RemoveRange(KeptChunks, _chunks.Count - KeptChunks);
        }
    }

    /// <summary>Copy <paramref name="name"/> in, and lend it until the next <see cref="Recall"/>.</summary>
    public ListedName Lend(ReadOnlySpan<char> name)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(name.Length, ChunkLength);

        // A name never runs from one chunk into the next.
        if ((_used % ChunkLength) + name.Length > ChunkLength)
        {
            _used += ChunkLength - (_used % ChunkLength);
        }

        var chunk = _used / ChunkLength;
        if (chunk == _chunks.Count)
        {
            _chunks.Add(new char[ChunkLength]);
        }

        var start = _used;
        name.CopyTo(_chunks[chunk].AsSpan(start % ChunkLength));
        _used += name.Length;

        return ListedName.Lent(_lending ??= new Lending(this), start, name.Length);
    }

    private ReadOnlySpan<char> Read(int start, int length) =>
        _chunks[start / ChunkLength].AsSpan(start % ChunkLength, length);

    /// <summary>
    /// The names lent between two recalls, each of which holds this rather than the memory itself, so
    /// that one recalled is told apart from one lent since at the same place. One for each directory a
    /// lending walk lists, where the strings it replaces were one for each name.
    /// </summary>
    internal sealed class Lending(ListingNames names)
    {
        private bool _ended;

        /// <summary>The <paramref name="length"/> characters lent at <paramref name="start"/>, while they are lent.</summary>
        public ReadOnlySpan<char> Read(int start, int length) =>
            _ended
                ? throw new InvalidOperationException(
                    "A name lent by a directory listing was read after the worker had listed another directory.")
                : names.Read(start, length);

        public void End() => _ended = true;
    }
}
