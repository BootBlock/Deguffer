namespace Deguffer.Core.Safety;

/// <summary>
/// How every store Deguffer keeps under <c>%LOCALAPPDATA%\Deguffer</c> is written: in full beside
/// the file, then moved over it in one step.
///
/// <para>A write straight into the file truncates it first, so a process stopped part-way, by a
/// crash, a shutdown or the elevated relaunch, leaves a torn file the next launch cannot read, and
/// everything the file held goes with it. Moved over, the file is always either the last whole
/// write or this one.</para>
///
/// <para>The caller keeps its own policy for a write that fails: an <see cref="IOException"/> or an
/// <see cref="UnauthorizedAccessException"/> reaches it unchanged, with the file as it was, and the
/// partial write beside it deleted wherever Windows allows.</para>
/// </summary>
public static class StoreFile
{
    /// <summary>Replace <paramref name="file"/> with <paramref name="contents"/>, creating its folder where it has none.</summary>
    public static void Replace(string file, string contents) =>
        Replace(file, stream =>
        {
            using var writer = new StreamWriter(stream);
            writer.Write(contents);
        });

    /// <summary>Replace <paramref name="file"/> with what <paramref name="write"/> puts in the stream it is given.</summary>
    public static void Replace(string file, Action<Stream> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentNullException.ThrowIfNull(write);

        if (Path.GetDirectoryName(file) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(LongPath.Extended(directory));
        }

        var written = file + ".tmp";

        try
        {
            using (var stream = new FileStream(LongPath.Extended(written), FileMode.Create, FileAccess.Write, FileShare.None))
            {
                write(stream);
            }

            File.Move(LongPath.Extended(written), LongPath.Extended(file), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(written);
            throw;
        }
    }

    private static void TryDelete(string written)
    {
        try
        {
            File.Delete(LongPath.Extended(written));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next write, which creates it afresh: the name is the store's own and
            // nothing reads it.
        }
    }
}
