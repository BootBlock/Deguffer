using Deguffer.Core.Providers;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// How a file another program wrote is read no further than a stated size: judged by what was read
/// rather than by the length Windows reports, and without allocating the whole limit for a small file.
/// </summary>
public sealed class BoundedFileTests : IDisposable
{
    private const int SolutionLimit = 16 * 1024 * 1024;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// A solution file is a few kilobytes against a 16 MB limit. Sizing the buffer by the limit zeroed
    /// 16 MB on the large object heap for each one.
    /// </summary>
    [Fact]
    public void A_small_file_under_a_large_limit_allocates_about_its_own_size()
    {
        var file = Write("small.sln", Bytes(1024));

        // The first read pays for type loading and compilation, which is not what is measured.
        _ = BoundedFile.ReadVerbatim(file, SolutionLimit);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var content = BoundedFile.ReadVerbatim(file, SolutionLimit);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(1024, content?.Length);
        Assert.True(allocated < 64 * 1024, $"Reading a 1 KB file allocated {allocated} bytes.");
    }

    [Fact]
    public void A_file_of_exactly_the_limit_is_read_whole()
    {
        var bytes = Bytes(4096);

        Assert.Equal(bytes, BoundedFile.ReadVerbatim(Write("exact.bin", bytes), 4096)?.ToArray());
    }

    [Fact]
    public void A_file_one_byte_past_the_limit_is_refused()
    {
        Assert.Null(BoundedFile.ReadVerbatim(Write("over.bin", Bytes(4097)), 4096));
    }

    [Fact]
    public void A_missing_file_is_null()
    {
        Assert.Null(BoundedFile.ReadVerbatim(Path.Combine(_temp.Path, "absent.bin"), 4096));
    }

    /// <summary>
    /// The owning program may be writing the file while it is read, so the length reported when it
    /// was opened is smaller than what it holds by the time the read reaches the end.
    /// </summary>
    [Fact]
    public void A_file_that_grows_past_the_limit_while_it_is_read_is_refused()
    {
        using var stream = new GrowingStream(Bytes(10_000), reportedLength: 100);

        Assert.Null(BoundedFile.ReadVerbatim(stream, 4096));
    }

    [Fact]
    public void A_file_that_grows_past_its_first_buffer_but_not_the_limit_is_read_whole()
    {
        var bytes = Bytes(10_000);
        using var stream = new GrowingStream(bytes, reportedLength: 100);

        Assert.Equal(bytes, BoundedFile.ReadVerbatim(stream, 16_384)?.ToArray());
    }

    [Fact]
    public void A_file_that_grows_to_exactly_the_limit_is_read_whole()
    {
        var bytes = Bytes(4096);
        using var stream = new GrowingStream(bytes, reportedLength: 0);

        Assert.Equal(bytes, BoundedFile.ReadVerbatim(stream, 4096)?.ToArray());
    }

    /// <summary>A file truncated after it was opened is judged by what was read, not what was reported.</summary>
    [Fact]
    public void A_file_shorter_than_its_reported_length_is_read_as_it_is()
    {
        var bytes = Bytes(50);
        using var stream = new GrowingStream(bytes, reportedLength: 1_000_000);

        Assert.Equal(bytes, BoundedFile.ReadVerbatim(stream, 4096)?.ToArray());
    }

    [Fact]
    public void Read_removes_a_byte_order_mark_and_ReadVerbatim_keeps_it()
    {
        var file = Write("marked.txt", [0xEF, 0xBB, 0xBF, (byte)'a']);

        Assert.Equal([(byte)'a'], BoundedFile.Read(file, 4096)?.ToArray());
        Assert.Equal([0xEF, 0xBB, 0xBF, (byte)'a'], BoundedFile.ReadVerbatim(file, 4096)?.ToArray());
    }

    /// <summary>A parser's stream reads the content in place, past a removed byte order mark included.</summary>
    [Fact]
    public void AsStream_reads_the_content_without_copying_it()
    {
        var bytes = Bytes(1024 * 1024);
        bytes[0] = 0xEF;
        bytes[1] = 0xBB;
        bytes[2] = 0xBF;
        var content = BoundedFile.Read(Write("large.xml", bytes), SolutionLimit)!.Value;

        _ = BoundedFile.AsStream(content);

        var before = GC.GetAllocatedBytesForCurrentThread();
        using var stream = BoundedFile.AsStream(content);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var copy = new MemoryStream();
        stream.CopyTo(copy);

        Assert.Equal(bytes[3..], copy.ToArray());
        Assert.True(allocated < 1024, $"A stream over 1 MB allocated {allocated} bytes.");
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_temp.Path, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Bytes(int count)
    {
        var bytes = new byte[count];

        for (var i = 0; i < count; i++)
        {
            bytes[i] = (byte)('a' + (i % 26));
        }

        return bytes;
    }

    /// <summary>
    /// A file whose reported length is not what it holds, handed out a little at a time as a file
    /// still being written is.
    /// </summary>
    private sealed class GrowingStream(byte[] content, long reportedLength) : Stream
    {
        private const int Chunk = 333;

        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => reportedLength;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var taken = Math.Min(Math.Min(count, Chunk), content.Length - _position);
            content.AsSpan(_position, taken).CopyTo(buffer.AsSpan(offset));
            _position += taken;
            return taken;
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
