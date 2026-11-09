using System.Collections.Concurrent;
using Deguffer.Core.Providers;

namespace Deguffer.Core.Tests;

/// <summary>A reader over the real test files that records which were opened and how far into them it read.</summary>
internal sealed class RecordedTranscriptReads
{
    private readonly ConcurrentQueue<string> _opened = new();
    private long _furthest;

    public RecordedTranscriptReads() => Reader = new ClaudeCodeTranscriptReader(Open);

    public ClaudeCodeTranscriptReader Reader { get; }

    /// <summary>Each path opened, in the form the file was opened by.</summary>
    public IReadOnlyCollection<string> Opened => _opened;

    /// <summary>The furthest byte of any transcript read so far.</summary>
    public long Furthest => Interlocked.Read(ref _furthest);

    private Stream Open(string path)
    {
        _opened.Enqueue(path);

        return new RecordingStream(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
            this);
    }

    private void Reached(long position)
    {
        long seen;

        do
        {
            seen = Interlocked.Read(ref _furthest);
        }
        while (position > seen && Interlocked.CompareExchange(ref _furthest, position, seen) != seen);
    }

    private sealed class RecordingStream(FileStream inner, RecordedTranscriptReads reads) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);

            if (read > 0)
            {
                reads.Reached(inner.Position);
            }

            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
