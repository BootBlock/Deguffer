namespace Deguffer.Core.Safety;

/// <summary>
/// The handle of the directory one walk worker listed last, kept open while the walk's callback reads
/// that directory's contents, so a file in it can be named to Windows by its bare name relative to the
/// handle (<see cref="StatByName"/>) rather than by a full path built and parsed again for each file.
///
/// <para>One for each worker, which lists one directory at a time. Not thread-safe.</para>
/// </summary>
internal sealed class ListedDirectoryHandle : IDisposable
{
    private nint _handle;

    /// <summary>The handle, or zero where none is held.</summary>
    public nint Handle => _handle;

    /// <summary>Hold <paramref name="handle"/>, closing the one held before it.</summary>
    public void Hold(nint handle)
    {
        Release();
        _handle = handle;
    }

    /// <summary>Close the handle held, if any.</summary>
    public void Release()
    {
        if (_handle != 0)
        {
            DirectoryQuery.Close(_handle);
            _handle = 0;
        }
    }

    public void Dispose() => Release();
}
