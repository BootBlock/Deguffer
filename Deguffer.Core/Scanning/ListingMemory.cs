using System.Runtime.InteropServices;

namespace Deguffer.Core.Scanning;

/// <summary>
/// One worker's memory for listing directories from the disk: the buffer Windows writes a directory's
/// entries into, and room for the name of the directory being opened. Made once for a worker and
/// reused for every directory it reads.
///
/// <para><b>Why a worker owns it.</b> A walk reads hundreds of thousands of directories, and listing
/// each through a new <see cref="System.IO.Enumeration.FileSystemEnumerator{TResult}"/> allocated and
/// freed a native buffer, a finalisable object, a lock and a closure for every one. Allocating and
/// freeing the buffer was also what made a large buffer slow: at 1 MiB, allocated for each directory,
/// a walk ran four to nine times slower, and reused it was level with 16 KiB (#370).</para>
///
/// <para>Not thread-safe: one worker uses it at a time. Dispose it once the worker has ended.</para>
/// </summary>
public sealed unsafe class ListingMemory : IDisposable
{
    /// <summary>Room for a directory's name before the first longer one, in characters.</summary>
    private const int FirstNameRoom = 512;

    private byte* _entries;
    private char[] _name = new char[FirstNameRoom];

    /// <param name="bytes">The most a listing through this memory may ask Windows for in one call.</param>
    public ListingMemory(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);

        // Aligned for the 64-bit fields of each entry Windows writes.
        _entries = (byte*)NativeMemory.AlignedAlloc((nuint)bytes, sizeof(long));
        Bytes = bytes;
    }

    /// <summary>The size of the entry buffer, which no listing through this memory may exceed.</summary>
    public int Bytes { get; }

    /// <summary>The entry buffer.</summary>
    internal byte* Entries => _entries is not null ? _entries : throw new ObjectDisposedException(nameof(ListingMemory));

    /// <summary>Room for a name of <paramref name="length"/> characters, grown where it has none.</summary>
    internal char[] NameRoom(int length)
    {
        if (_name.Length < length)
        {
            _name = new char[Math.Max(length, _name.Length * 2)];
        }

        return _name;
    }

    public void Dispose()
    {
        NativeMemory.AlignedFree(_entries);
        _entries = null;
    }
}
