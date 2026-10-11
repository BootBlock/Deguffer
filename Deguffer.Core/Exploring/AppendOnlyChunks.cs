using System.Numerics;
using System.Runtime.CompilerServices;

namespace Deguffer.Core.Exploring;

/// <summary>
/// An array that only grows, held in chunks of one fixed length so that growing it never moves what
/// it already holds.
///
/// <para>What lets a reader use what was written without holding the writers' lock. A
/// <see cref="List{T}"/> copies its whole backing array each time it grows, so a reader outside the
/// lock could be reading an array that has just been replaced. A chunk here is never replaced once
/// allocated, so a <see cref="Prefix"/> taken under the lock stays valid after it is released, for
/// every slot it covers, however much is added after it.</para>
///
/// <para>Each chunk is kept under the 85,000 bytes that put an array on the Large Object Heap, which
/// is collected only with the oldest generation. A tree of millions of nodes is otherwise one large
/// array per column, reallocated at every doubling.</para>
///
/// <para>Not safe for concurrent writes: the caller serialises <see cref="Add"/> and any write
/// through the indexer.</para>
/// </summary>
internal sealed class AppendOnlyChunks<T>
{
    private const int LargeObjectBytes = 85_000;

    // The largest power of two whose chunk stays off the Large Object Heap, with room for the array's
    // own header.
    private static readonly int ChunkShift =
        BitOperations.Log2((uint)((LargeObjectBytes - 64) / Unsafe.SizeOf<T>()));

    private static readonly int ChunkMask = (1 << ChunkShift) - 1;

    private T[][] _chunks = [];

    public int Count { get; private set; }

    /// <summary>A slot already added, to read or to change.</summary>
    public ref T this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);

            return ref _chunks[index >> ChunkShift][index & ChunkMask];
        }
    }

    /// <summary>Add a slot at the end, holding the default value, and return it to be filled in.</summary>
    public ref T Add()
    {
        var chunk = Count >> ChunkShift;

        if (chunk == _chunks.Length)
        {
            // A new directory array rather than a resized one in place: a Prefix already handed out
            // keeps the old one, which still names every chunk it covers.
            var grown = new T[Math.Max(4, _chunks.Length * 2)][];
            _chunks.CopyTo(grown, 0);
            _chunks = grown;
        }

        _chunks[chunk] ??= new T[1 << ChunkShift];

        return ref _chunks[chunk][Count++ & ChunkMask];
    }

    /// <summary>
    /// The slots added so far, to be read from any thread once the caller's lock has published them.
    /// A slot changed through the indexer after this is taken may be seen either way, so a caller
    /// that changes slots reads only the parts it never changes.
    /// </summary>
    public Prefix Freeze() => new(_chunks, Count);

    /// <summary>The first <see cref="Count"/> slots, as they were when <see cref="Freeze"/> was called.</summary>
    public readonly struct Prefix(T[][] chunks, int count)
    {
        public int Count => count;

        public ref readonly T this[int index]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);

                return ref chunks[index >> ChunkShift][index & ChunkMask];
            }
        }
    }
}
