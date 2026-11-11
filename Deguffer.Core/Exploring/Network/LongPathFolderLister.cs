using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Exploring.Network;

/// <summary>
/// <see cref="IFolderLister"/> through the listing every walk takes (<see cref="IFileSystem.ListEntries"/>):
/// one level of a folder, asked in the extended form (§6.3), read as names alone. No file is
/// opened, and the listing keeps no file at all.
///
/// <para>Hidden and system folders are left out, as File Explorer leaves them out by default, so
/// <c>$RECYCLE.BIN</c> and <c>System Volume Information</c> are not offered as places to scan. A
/// person who wants one types it.</para>
/// </summary>
public sealed class LongPathFolderLister(IFileSystem fs) : IFolderLister
{
    /// <summary>The one instance the app runs with (G5), over the disk itself.</summary>
    public static LongPathFolderLister Default { get; } = new(WindowsFileSystem.Default);

    private const FileAttributes LeftOut = FileAttributes.Hidden | FileAttributes.System;

    public NetworkListing<string> Folders(string folder, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);

        var entries = new List<WalkEntry>();
        using var memory = new ListingMemory(WalkTuning.MinimumListingBuffer);
        var request = new ListingRequest(memory, memory.Bytes, FollowsLink: true, FileNames.None);
        var end = fs.ListEntries(LongPath.Extended(folder), request, entries);

        ct.ThrowIfCancellationRequested();

        // A refusal is never an empty folder (§5.3), and half a listing is not offered as the whole.
        return end switch
        {
            { Presence: PathPresence.Present } => NetworkListing<string>.Listed(
                [.. entries.Where(entry => entry.IsDirectory && (entry.Attributes & LeftOut) == 0).Select(entry => entry.Name)]),
            { Presence: PathPresence.Absent } => NetworkListing<string>.Failed(NetworkFailure.NotFound),
            { Refusal: ListingRefusal.Denied } => NetworkListing<string>.Failed(NetworkFailure.NeedsSignIn),
            _ => NetworkListing<string>.Failed(NetworkFailure.Refused),
        };
    }
}
