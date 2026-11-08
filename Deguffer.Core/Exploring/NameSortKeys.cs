using System.Collections.Concurrent;

namespace Deguffer.Core.Exploring;

/// <summary>
/// Puts siblings in name order (<see cref="ExploreTree.CompareByName(int[], NamePool.Frozen, int, int)"/>)
/// by a number made once for each name, and reads names only where two numbers tie.
///
/// <para>Most siblings on a real volume share their size with another, because a size is allocated
/// clusters: on one system volume of 2.6 million nodes, four in five siblings were in a run of one
/// size, and putting those runs in name order by comparing the names was nine tenths of the sort
/// (#553). The cost was where the names are rather than what comparing them does: the pool holds
/// names in the order the reader first met them, so siblings' names lie far apart in tens of
/// megabytes, and each name a comparison read was a read from memory rather than the cache.
/// Numbering the nodes so that siblings were adjacent made no difference. Each name is made a
/// number here in one pass through the pool in the order it holds them, and a sort then reads those
/// numbers.</para>
///
/// <para>A number is the name's first <see cref="KeyLength"/> characters, upper-cased, a byte each,
/// the first in the highest byte. It orders as the names do wherever two of them differ, because
/// ordinal comparison without regard to case keeps the ASCII characters among themselves: it maps a
/// lower-case ASCII letter to its capital, leaves every other ASCII character as it is, and orders
/// every character past ASCII after every ASCII one (<c>NameSortKeysTests</c> holds the runtime to
/// that). So the number stops at the first character past ASCII, with a byte above every ASCII
/// character for it, and two names whose numbers tie are compared whole: names alike for sixteen
/// characters, or alike up to a character past ASCII.</para>
///
/// <para>Sixteen characters because on that volume the numbers then left 1.5 million of the 17
/// million comparisons to make whole, most of them in folders whose names share a long prefix, such
/// as <c>WinSxS</c>. Eight left 5.5 million and took twice as long. The numbers take 16 bytes a
/// distinct name for as long as the sort runs: 22 MB for that volume's 1.36 million names.</para>
/// </summary>
internal sealed class NameSortKeys
{
    /// <summary>How many of each name's characters its number holds.</summary>
    private const int KeyLength = 16;

    /// <summary>
    /// The byte for a character past ASCII, above every ASCII character and so above every byte an
    /// upper-cased ASCII character is.
    /// </summary>
    private const byte PastAscii = 0x80;

    /// <summary>Names numbered in a pass at a time, so a thread takes a share large enough to stream.</summary>
    private const int NamesPerShare = 16384;

    private readonly UInt128[] _keys;

    private readonly int[] _nameNumbers;

    private readonly Comparison<int> _byName;

    private NameSortKeys(UInt128[] keys, int[] nameNumbers, Comparison<int> byName)
    {
        _keys = keys;
        _nameNumbers = nameNumbers;
        _byName = byName;
    }

    /// <summary>
    /// The number of every name in <paramref name="names"/>, made on up to <paramref name="workers"/>
    /// threads, for ordering the nodes <paramref name="nameNumbers"/> names.
    /// </summary>
    public static NameSortKeys Of(int[] nameNumbers, NamePool.Frozen names, int workers, CancellationToken ct)
    {
        var keys = new UInt128[names.Count];

        Parallel.ForEach(
            Partitioner.Create(0, keys.Length, NamesPerShare),
            new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct },
            share =>
            {
                for (var number = share.Item1; number < share.Item2; number++)
                {
                    keys[number] = KeyOf(names[number]);
                }
            });

        return new NameSortKeys(
            keys, nameNumbers, (left, right) => ExploreTree.CompareByName(nameNumbers, names, left, right));
    }

    /// <summary>
    /// Put <paramref name="siblings"/> in name order, in place.
    /// </summary>
    /// <param name="keyBuffer">
    /// Room for at least one number per sibling, overwritten. Passed in so that a caller ordering many
    /// lists allocates it once.
    /// </param>
    public void Order(Span<int> siblings, Span<UInt128> keyBuffer)
    {
        var length = siblings.Length;
        var keys = keyBuffer[..length];

        for (var i = 0; i < length; i++)
        {
            keys[i] = _keys[_nameNumbers[siblings[i]]];
        }

        MemoryExtensions.Sort(keys, siblings);

        for (int start = 0, end; start < length; start = end)
        {
            for (end = start + 1; end < length && keys[end] == keys[start]; end++)
            {
            }

            if (end - start > 1)
            {
                MemoryExtensions.Sort(siblings[start..end], _byName);
            }
        }
    }

    /// <summary>
    /// The number of <paramref name="name"/>. A name shorter than <see cref="KeyLength"/> leaves the
    /// bytes past its end zero, below every character, so it comes before every longer name it
    /// begins, as it does compared whole. So do the bytes past a character beyond ASCII, which leaves
    /// two names alike that far to be compared whole.
    /// </summary>
    internal static UInt128 KeyOf(ReadOnlySpan<char> name)
    {
        UInt128 key = 0;
        var end = Math.Min(name.Length, KeyLength);
        var used = 0;

        while (used < end)
        {
            var c = name[used++];

            if (!char.IsAscii(c))
            {
                key = (key << 8) | PastAscii;
                break;
            }

            key = (key << 8) | char.ToUpperInvariant(c);
        }

        return key << (8 * (KeyLength - used));
    }
}
