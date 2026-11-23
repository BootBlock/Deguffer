using System.Collections;
using Deguffer.Core.Exploring;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// Values gathered in any order, each under the number of the record it came from, and handed back
/// in record order without moving the values themselves.
///
/// <para>The first pass gathers the records it holds back and the extension records it keeps from
/// every parse thread at once, so they arrive out of order, and the second pass merges them by record
/// number. Sorting the values would move each one at every swap, and an
/// <see cref="MftExtensionRecord"/> is a 200-byte value, while comparing held-back records reaches
/// through a reference on each side. Only a 16-byte key is sorted here: the record number and where
/// the value was put.</para>
///
/// <para>Held in <see cref="AppendOnlyChunks{T}"/>, so growing never copies what is already held and
/// no chunk lands on the Large Object Heap. A <see cref="List{T}"/> of 200-byte values grows by
/// doubling to one large array, about two and a half times the size of what it holds.</para>
///
/// <para>Not safe for concurrent writes: the caller serialises <see cref="Add"/>.</para>
/// </summary>
internal sealed class MftByRecord<T>
{
    private readonly AppendOnlyChunks<T> _values = new();
    private readonly AppendOnlyChunks<long> _numbers = new();

    public void Add(long number, in T value)
    {
        _values.Add() = value;
        _numbers.Add() = number;
    }

    /// <summary>
    /// Everything added, in record order. Values added under one number keep the order they were
    /// added in, so the order never depends on how the sort happened to split them. Only once
    /// nothing more will be added.
    /// </summary>
    public Sorted InRecordOrder()
    {
        var order = new Key[_values.Count];

        for (var i = 0; i < order.Length; i++)
        {
            order[i] = new Key(_numbers[i], i);
        }

        Array.Sort(order);
        return new Sorted(_values.Freeze(), order);
    }

    /// <summary>What was added, read in record order through the sorted keys.</summary>
    internal sealed class Sorted : IReadOnlyList<T>
    {
        private readonly AppendOnlyChunks<T>.Prefix _values;
        private readonly Key[] _order;

        public Sorted(AppendOnlyChunks<T>.Prefix values, Key[] order)
        {
            _values = values;
            _order = order;
        }

        public int Count => _order.Length;

        /// <summary>The value at <paramref name="index"/> in record order, read where it was put rather than copied.</summary>
        public ref readonly T this[int index] => ref _values[_order[index].Slot];

        T IReadOnlyList<T>.this[int index] => this[index];

        /// <summary>The number of the record the value at <paramref name="index"/> came from, read from the key alone.</summary>
        public long Number(int index) => _order[index].Number;

        public IEnumerator<T> GetEnumerator()
        {
            for (var i = 0; i < _order.Length; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>A value's record number, and where it was put, which breaks a tie between two values under one number.</summary>
    internal readonly record struct Key(long Number, int Slot) : IComparable<Key>
    {
        public int CompareTo(Key other) =>
            Number != other.Number ? Number.CompareTo(other.Number) : Slot.CompareTo(other.Slot);
    }
}
