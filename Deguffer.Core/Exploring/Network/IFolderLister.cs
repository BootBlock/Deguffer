namespace Deguffer.Core.Exploring.Network;

/// <summary>
/// The folders one level inside a share or a folder, by name. Behind a seam so a test describes the
/// folders, and never reads a share.
/// </summary>
public interface IFolderLister
{
    /// <summary>
    /// The names of the folders directly inside <paramref name="folder"/>. Names only: no file is
    /// opened and no file's content is read. Throws <see cref="OperationCanceledException"/> rather
    /// than answer once <paramref name="ct"/> is cancelled, so a listing nobody waits for is not kept
    /// as an answer.
    /// </summary>
    /// <param name="folder">The folder in display form. The implementation asks in the extended form (§6.3).</param>
    NetworkListing<string> Folders(string folder, CancellationToken ct);
}
