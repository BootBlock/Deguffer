namespace Deguffer.Core.Tests.Fakes;

/// <summary>A scratch tree that removes itself, so provider tests can build a real filesystem.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deguffer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Create a directory under the scratch root and return its full path.</summary>
    public string CreateDirectory(params string[] segments)
    {
        var full = System.IO.Path.Combine([Path, .. segments]);
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>Create a file of <paramref name="bytes"/> length and return its full path.</summary>
    public string CreateFile(int bytes, params string[] segments)
    {
        var full = System.IO.Path.Combine([Path, .. segments]);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[bytes]);
        return full;
    }

    /// <summary>
    /// Push a file's creation and last-write times back by <paramref name="by"/>, and return it.
    ///
    /// Both, because <see cref="Safety.MinimumAge"/> takes the newer of the two — a fixture that
    /// moved only the last-write time would leave every file it aged still protected by its
    /// creation time, and every test built on it would pass for the wrong reason.
    /// </summary>
    public static string Age(string path, TimeSpan by)
    {
        var when = DateTime.UtcNow - by;

        File.SetCreationTimeUtc(Safety.LongPath.Extended(path), when);
        File.SetLastWriteTimeUtc(Safety.LongPath.Extended(path), when);

        return path;
    }

    public void Dispose()
    {
        try
        {
            // Extended form: tests deliberately build trees past MAX_PATH, and cleanup has to
            // reach them too.
            Directory.Delete(Safety.LongPath.Extended(Path), recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // A leaked scratch directory is not worth failing a test run over.
        }
    }
}
