using Deguffer.Core.Duplicates;
using Deguffer.Core.Safety;
using Deguffer.Testing;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Tests;

/// <summary>
/// How a file's named streams are listed for a duplicate's comparison (§7.4): each copy removed lists
/// its streams and the kept copy's, and lists its own again just before it goes, so a listing
/// allocates nothing a comparison does not keep, and a listing too long for the first buffer is still
/// read whole.
/// </summary>
public sealed class NamedStreamListingTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// Most files hold no named stream, and a removed copy is listed in its comparison and again just
    /// before it goes, both measured here. Even an empty list is an allocation, so the bound is none
    /// at all.
    /// </summary>
    [Fact]
    public void Comparing_two_files_with_no_named_streams_allocates_nothing()
    {
        using var kept = Open(Write("kept.bin", "the same bytes"));
        using var copy = Open(Write("copy.bin", "the same bytes"));
        var length = new FileInfo(Path.Combine(_temp.Path, "kept.bin")).Length;

        // The first comparison pays for type loading, compilation and the pooled read buffers.
        Compare(kept, copy, length);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var outcome = Compare(kept, copy, length);
        var unchanged = CopyComparison.StreamsOf(copy) is { } after && CopyComparison.SameStreams(outcome.Streams, after);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(outcome.Check);
        Assert.Empty(outcome.Streams);
        Assert.True(unchanged);
        Assert.Equal(0, allocated);
    }

    /// <summary>
    /// A listing that does not fit the first buffer is asked again into a larger one. A listing cut
    /// at the first buffer would leave out a stream the kept copy lacks.
    /// </summary>
    [Fact]
    public void A_listing_longer_than_the_first_buffer_is_read_whole()
    {
        const int Count = 60;
        var file = Write("many.bin", "content");

        for (var i = 0; i < Count; i++)
        {
            File.WriteAllText($"{LongPath.Extended(file)}:{i:D3}{new string('s', 120)}", new string('x', i));
        }

        using var handle = Open(file);

        var streams = CopyComparison.StreamsOf(handle);

        Assert.NotNull(streams);
        Assert.Equal(Count, streams.Count);

        for (var i = 0; i < Count; i++)
        {
            Assert.Contains(new NamedStream($":{i:D3}{new string('s', 120)}:$DATA", i), streams);
        }
    }

    [Fact]
    public void Where_a_download_came_from_is_left_out_and_every_other_stream_is_listed()
    {
        var file = Write("downloaded.bin", "content");
        File.WriteAllText(LongPath.Extended(file) + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        File.WriteAllText(LongPath.Extended(file) + ":notes", "kept");

        using var handle = Open(file);

        Assert.Equal([new NamedStream(":notes:$DATA", 4)], CopyComparison.StreamsOf(handle));
    }

    private static ((RemovalCheck Check, string Why)? Check, IReadOnlyList<NamedStream> Streams) Compare(
        SafeFileHandle kept, SafeFileHandle copy, long length)
    {
        var check = CopyComparison.Compare(kept, length, copy, length, out var streams, CancellationToken.None);
        return (check, streams);
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_temp.Path, name);
        File.WriteAllText(LongPath.Extended(path), content);
        return path;
    }

    private static SafeFileHandle Open(string path) =>
        File.OpenHandle(LongPath.Extended(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
