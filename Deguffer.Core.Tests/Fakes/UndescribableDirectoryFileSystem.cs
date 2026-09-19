using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// The real filesystem, except that Windows will not say what is at one directory.
///
/// <para>The condition is real and reachable two ways — an access rule on a directory and on its
/// parent, and a directory symbolic link Windows declines to follow, which answers
/// <c>ERROR_UNTRUSTED_MOUNT_POINT</c> for everything below it. Neither can be arranged against the
/// real filesystem inside a removal test: the first needs a DACL on the scratch tree's own parent,
/// and the second depends on machine policy a test may not set.</para>
///
/// <para>What it is for is the claim a removal makes when it is over. "The directory is gone" was
/// read off a two-state existence check, which answers false for a directory that is standing and
/// refusing — so a removal that reached nothing at all reported its root as taken.</para>
/// </summary>
public sealed class UndescribableDirectoryFileSystem(IFileSystem inner, string refused) : IFileSystem
{
    /// <summary>Every path this fake was asked to describe, in the form it was asked in (§6.3).</summary>
    public List<string> Probed { get; } = [];

    // Delegated rather than derived from the probe below, because WindowsFileSystem.DirectoryExists
    // does not extend and ProbeDirectory does — a fake that closed that gap would hide it.
    public bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public PathPresence ProbeDirectory(string path)
    {
        Probed.Add(path);

        return IsRefused(path) ? PathPresence.Refused : inner.ProbeDirectory(path);
    }

    /// <summary>
    /// True for the refused directory, because that is what <see cref="LongPath.IsReparsePoint"/>
    /// answers for a path it could not read: it fails closed. Delegating instead would answer false
    /// for the real directory underneath, and a caller that asked this before the probe would look
    /// correct against the fake while reading a refusal as a link against Windows.
    /// </summary>
    public bool IsReparsePoint(string path) =>
        IsRefused(path) || inner.IsReparsePoint(path);

    public IReadOnlyList<FileSystemEntry> EnumerateEntries(string directory) => inner.EnumerateEntries(directory);

    public long? TryGetFileLength(string path) => inner.TryGetFileLength(path);

    public long? TryGetNewestFileTime(string path) => inner.TryGetNewestFileTime(path);

    public void DeleteFile(string path) => inner.DeleteFile(path);

    public RefusalReason? ProbeRemoval(string path) => inner.ProbeRemoval(path);

    public void DeleteDirectory(string path) => inner.DeleteDirectory(path);

    public void ClearAttributes(string path) => inner.ClearAttributes(path);

    public FileAttributes? TryGetAttributes(string path) => inner.TryGetAttributes(path);

    public bool MayExist(string path) => inner.MayExist(path);

    private bool IsRefused(string path) =>
        LongPath.Display(path).Equals(refused, StringComparison.OrdinalIgnoreCase);
}
