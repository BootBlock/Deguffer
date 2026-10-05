namespace Deguffer.Core.Scanning;

/// <summary>
/// How hard the walk drives the disk: how many directories it lists at once, and how many bytes of
/// entries it asks Windows for in each call.
///
/// <para>Neither value changes what the walk finds, only how fast it finds it. The right values
/// differ by drive: concurrent reads compete for the head of a spinning disk, an NVMe drive needs many
/// in flight to reach its speed, and a network share pays a round trip per call.</para>
/// </summary>
internal sealed record WalkTuning
{
    public const int MinimumThreads = 1;

    public const int MaximumThreads = 64;

    /// <summary>The smallest buffer the runtime will use. It reads anything less as this.</summary>
    public const int MinimumListingBuffer = 4 * 1024;

    /// <summary>Room for roughly ten thousand entries in one call, which few directories exceed.</summary>
    public const int MaximumListingBuffer = 1024 * 1024;

    /// <summary>
    /// What every scan uses. The thread count is the value the walk used before it could be set,
    /// and the buffer is what measurement favoured on the drives it was tried on.
    /// </summary>
    public static readonly WalkTuning Default = new(
        Math.Min(Environment.ProcessorCount * 2, 16),
        64 * 1024);

    public WalkTuning(int threads, int listingBufferBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threads, MinimumThreads);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(threads, MaximumThreads);
        ArgumentOutOfRangeException.ThrowIfLessThan(listingBufferBytes, MinimumListingBuffer);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(listingBufferBytes, MaximumListingBuffer);

        Threads = threads;
        ListingBufferBytes = listingBufferBytes;
    }

    /// <summary>How many directories are listed at once, the calling thread included.</summary>
    public int Threads { get; }

    /// <summary>How many bytes of entries each call to list a directory asks for.</summary>
    public int ListingBufferBytes { get; }
}
