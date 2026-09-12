using System.Runtime.CompilerServices;

namespace Deguffer.Core.Memory;

/// <summary>
/// Snapshots on a fixed cadence, for a view that keeps a picture of memory current.
///
/// <para><b>Never two reads at once, and never one straight after another.</b> The next read starts a
/// full <see cref="Cadence"/> after the last one finished, however long it took, so a machine slow to
/// answer is asked less often rather than continuously (§7.2).</para>
///
/// <para>Each read runs off the caller's thread. The process table alone is about a megabyte of
/// parsing, and the caller is a window.</para>
/// </summary>
public sealed class MemoryFeed(IMemorySource source, TimeProvider time)
{
    /// <summary>
    /// Two seconds: often enough that a program opening or closing shows up while the reader is still
    /// looking for it, and seldom enough that a picture of five hundred processes can be read at all.
    /// </summary>
    public static readonly TimeSpan Cadence = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A snapshot now, and another every <see cref="Cadence"/> after each one arrives, until
    /// <paramref name="ct"/> is cancelled. Cancelling ends the sequence with
    /// <see cref="OperationCanceledException"/> and starts no further read.
    /// </summary>
    public async IAsyncEnumerable<MemorySnapshot> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            yield return await Task.Run(() => source.Read(ct), ct).ConfigureAwait(false);

            await Task.Delay(Cadence, time, ct).ConfigureAwait(false);
        }
    }
}
