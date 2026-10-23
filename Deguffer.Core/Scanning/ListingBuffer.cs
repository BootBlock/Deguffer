using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>
/// How many bytes of entries one walk asks Windows for in each call, and the smaller size it falls
/// back to where a server rejects it.
///
/// <para>Some network servers reject a large directory query buffer. A listing that fails that way
/// cannot be told from a refusal by its error alone, and reporting it as one would leave every total
/// above the folder short. So a listing refused while it was read is tried again with the smallest
/// buffer, and once the smaller buffer has listed a folder the larger one could not, the rest of the
/// walk asks for the smaller one from the start: one server answers every folder on the share the
/// same way.</para>
///
/// <para>A folder that could not be opened is never tried again, and nor is one the account may not
/// read. The buffer is only used once the folder is open, so neither a sharing violation at the open
/// nor the refusal a volume holds hundreds of is changed by its size.</para>
/// </summary>
internal sealed class ListingBuffer
{
    private const int Smallest = WalkTuning.MinimumListingBuffer;

    private int _rejected;

    public ListingBuffer(int bytes) => Largest = Math.Max(bytes, Smallest);

    /// <summary>The most any listing asks for, which is what each worker's memory holds.</summary>
    public int Largest { get; }

    /// <summary>What the next listing asks for.</summary>
    public int Bytes => Volatile.Read(ref _rejected) == 1 ? Smallest : Largest;

    /// <summary>
    /// The size to try a listing again with, or null where trying again cannot change how it ended.
    /// </summary>
    public int? RetryAfter(int used, ListingEnd end) =>
        end is { Presence: PathPresence.Refused, WhileReading: true, Refusal: not ListingRefusal.Denied } && used > Smallest
            ? Smallest
            : null;

    /// <summary>The smaller buffer listed a folder the larger one could not.</summary>
    public void Rejected() => Volatile.Write(ref _rejected, 1);
}
