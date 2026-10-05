using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>
/// Which listing buffer one walk asks for, and the smaller one it falls back to where a server
/// rejects it.
///
/// <para>Some network servers reject a large directory query buffer. A listing that fails that way
/// cannot be told from a refusal by its error alone, and reporting it as one would leave every total
/// above the folder short. So a refused listing is tried again with the smallest buffer, and once the
/// smaller buffer has listed a folder the larger one could not, the rest of the walk asks for the
/// smaller one from the start: one server answers every folder on the share the same way.</para>
///
/// <para>A folder the account may not read is never tried again. It is the refusal a volume holds
/// hundreds of, and no buffer changes it.</para>
/// </summary>
internal sealed class ListingBuffer
{
    private static readonly EnumerationOptions Smallest =
        DirectoryListing.EveryEntryWithBuffer(WalkTuning.MinimumListingBuffer);

    private readonly EnumerationOptions _requested;

    private int _rejected;

    public ListingBuffer(int bytes) =>
        _requested = bytes > WalkTuning.MinimumListingBuffer ? DirectoryListing.EveryEntryWithBuffer(bytes) : Smallest;

    /// <summary>What the next listing asks for.</summary>
    public EnumerationOptions Options => Volatile.Read(ref _rejected) == 1 ? Smallest : _requested;

    /// <summary>
    /// The options to try a listing again with, or null where trying again cannot change how it
    /// ended.
    /// </summary>
    public EnumerationOptions? RetryAfter(EnumerationOptions used, ListingEnd end) =>
        end.Presence is PathPresence.Refused && !end.Denied && !ReferenceEquals(used, Smallest)
            ? Smallest
            : null;

    /// <summary>The smaller buffer listed a folder the larger one could not.</summary>
    public void Rejected() => Volatile.Write(ref _rejected, 1);
}
