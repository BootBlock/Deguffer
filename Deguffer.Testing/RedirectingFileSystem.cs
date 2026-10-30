using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Testing;

/// <summary>
/// The real filesystem, except that Windows gives <paramref name="final"/> as the final path of
/// <paramref name="redirected"/>. A volume mounted nowhere a user can name answers only with its
/// <c>\\?\Volume{GUID}\</c> name, and a test cannot mount a volume.
/// </summary>
/// <param name="redirected">The path whose final path is stated, in display form.</param>
public sealed class RedirectingFileSystem(IFileSystem inner, string redirected, string final) : IFileSystem
{
    public bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public PathPresence ProbeDirectory(string path) => inner.ProbeDirectory(path);

    public PathPresence ProbeEntry(string path) => inner.ProbeEntry(path);

    public bool IsReparsePoint(string path) => inner.IsReparsePoint(path);

    public ListingEnd ListEntries(string directory, ListingRequest request, List<WalkEntry> into) =>
        inner.ListEntries(directory, request, into);

    public long? TryGetFileLength(string path) => inner.TryGetFileLength(path);

    public long? TryGetNewestFileTime(string path) => inner.TryGetNewestFileTime(path);

    public void DeleteFile(string path) => inner.DeleteFile(path);

    public RefusalReason? ProbeRemoval(string path) => inner.ProbeRemoval(path);

    public void DeleteDirectory(string path) => inner.DeleteDirectory(path);

    public void ClearAttributes(string path) => inner.ClearAttributes(path);

    public FileAttributes? TryGetAttributes(string path) => inner.TryGetAttributes(path);

    public bool MayExist(string path) => inner.MayExist(path);

    public string? FinalPath(string asked) =>
        LongPath.Display(asked).Equals(redirected, StringComparison.OrdinalIgnoreCase) ? final : inner.FinalPath(asked);
}
