using Deguffer.Core.Safety;

namespace Deguffer.Core.Cloud;

/// <summary>
/// What Windows' Cloud Files API says about the sync roots on this machine and the placeholders
/// inside them, and the one change Deguffer asks of it.
///
/// <para><b>A seam because every answer here is about somebody's cloud account.</b> A test cannot
/// shape a real sync root into a file with unsynced edits under a folder the user pinned, and the
/// rules that keep those files on the disk are the whole safety argument for releasing anything.
/// <see cref="CloudFiles"/> is the real one, and its own tests register a scratch sync root to prove
/// the calls behave as this contract says.</para>
///
/// <para><b>Nothing here reads a file's data, so nothing here can download one.</b> Listing a folder
/// reads the directory's own index, and describing a placeholder opens it for its attributes alone.
/// Hydration happens on a read of the data, and no member reads any.</para>
/// </summary>
public interface ICloudFiles
{
    /// <summary>The sync roots registered for the signed-in user, in the order Windows lists them.</summary>
    IReadOnlyList<SyncRoot> SyncRoots();

    /// <summary>Whether the sync app behind <paramref name="syncRoot"/> is running and connected to it.</summary>
    SyncProviderState ProviderState(string syncRoot);

    /// <summary>
    /// The entries directly inside <paramref name="directory"/>, each classified from the directory's
    /// own index. Empty where the directory cannot be listed, which the caller learns from
    /// <see cref="Read"/> rather than from here.
    /// </summary>
    IEnumerable<CloudEntry> List(string directory, CancellationToken ct);

    /// <summary>What <paramref name="path"/> is now: whether it is there, and its placeholder state where it is one.</summary>
    PlaceholderReading Read(string path);

    /// <summary>
    /// Mark one placeholder as not needed on this PC, so its sync app may release its local copy, and
    /// only if <paramref name="stillEligible"/> holds of it at that moment.
    ///
    /// <para><b>One handle for both halves.</b> The file is described and unpinned through the same
    /// open handle, so a file that gained unsynced edits between the preview and the clean is read as
    /// it now is, not as it was.</para>
    ///
    /// <para><b>Unpinning is all this does.</b> <c>CfSetPinState</c> is the call Microsoft opens to
    /// any application. <c>CfDehydratePlaceholder</c> is the sync app's own, and it carries a
    /// data-corruption warning for a caller without an exclusive handle, so it is never used.</para>
    /// </summary>
    ReleaseAnswer Release(string path, Func<Placeholder, bool> stillEligible);
}

/// <summary>One registered sync root.</summary>
/// <param name="Id">
/// The identifier the sync app registered, <c>provider!SID!account</c> in the form Microsoft
/// documents for <c>StorageProviderSyncRootInfo.Id</c>.
/// </param>
/// <param name="Path">The folder the sync app keeps in step with the cloud, in display form.</param>
/// <param name="DisplayName">What the sync app calls it, such as <c>OneDrive - Personal</c>.</param>
public sealed record SyncRoot(string Id, string Path, string DisplayName)
{
    /// <summary>
    /// The sync app that registered this root: the first part of <see cref="Id"/>. The Cloud Files API
    /// reports the same name as the root's provider, which a scratch root registered as
    /// <c>DegufferProbe!…</c> showed.
    /// </summary>
    public string ProviderName => Id.Split('!', 2)[0];
}

/// <summary>Whether a sync app would act on a request to release a file.</summary>
public enum SyncProviderState
{
    /// <summary>
    /// Connected. Microsoft's own states for a working sync app (idle, populating, syncing) and
    /// the one for lost connectivity all count, because a local copy is released without the network.
    /// </summary>
    Running,

    /// <summary>
    /// Nothing is connected to the root, or what was connected stopped or failed. A request made now
    /// is recorded and nothing is released until the sync app runs again.
    /// </summary>
    NotRunning,

    /// <summary>Windows would not say. Treated as <see cref="NotRunning"/> by everything that acts.</summary>
    Unknown,
}

/// <summary>One entry of a directory listing, classified without opening it.</summary>
/// <param name="Path">The entry, in display form.</param>
/// <param name="IsPlaceholder">
/// Whether the Cloud Files API reads it as a placeholder from its attributes and reparse tag. The
/// pinned and unpinned attributes are deliberately not the test: anyone can set them, and a
/// whole-profile walk found two files carrying one with no placeholder behind them.
/// </param>
/// <param name="IsOtherLink">
/// A junction, a symbolic link or any reparse point that is not a placeholder. Never entered and
/// never acted on, for the reason <see cref="Execution.DirectoryRemover"/> does not follow one.
/// </param>
/// <param name="NewestFileTime">The newer of its creation and last-write FILETIMEs.</param>
public readonly record struct CloudEntry(
    string Path,
    bool IsDirectory,
    bool IsPlaceholder,
    bool IsOtherLink,
    long NewestFileTime);

/// <summary>A placeholder's state, as <c>CfGetPlaceholderInfo</c> reports it.</summary>
/// <param name="OnDiskBytes">The bytes of the file's data held on this PC, which is what releasing it gives back.</param>
/// <param name="ModifiedBytes">Bytes written here that the cloud does not have yet.</param>
/// <param name="InSync">Whether the sync app has marked the file as matching the cloud.</param>
/// <param name="NewestFileTime">The newer of its creation and last-write FILETIMEs.</param>
public sealed record Placeholder(
    long OnDiskBytes,
    long ModifiedBytes,
    bool InSync,
    PinState Pin,
    long NewestFileTime);

/// <summary>
/// The pin a placeholder carries itself. The values are the Cloud Files API's own, so a state that
/// arrives unrecognised is read as <see cref="Pinned"/>, the one that leaves the file alone.
/// </summary>
public enum PinState
{
    /// <summary>No choice of its own. A file with this pin follows the nearest folder above it that has one.</summary>
    Unspecified = 0,

    /// <summary>"Always keep on this device". The user asked for the file to stay.</summary>
    Pinned = 1,

    /// <summary>Already marked as not needed on this PC, so its sync app has already been asked to release it.</summary>
    Unpinned = 2,

    /// <summary>Excluded from sync altogether. The cloud may not hold it.</summary>
    Excluded = 3,
}

/// <summary>What <see cref="ICloudFiles.Read"/> found.</summary>
/// <param name="Presence">Whether the path is there, in the three answers every probe here gives.</param>
/// <param name="Placeholder">Its placeholder state, or null where it is present and not a placeholder, or not present.</param>
public sealed record PlaceholderReading(PathPresence Presence, Placeholder? Placeholder)
{
    public static readonly PlaceholderReading Absent = new(PathPresence.Absent, null);

    public static readonly PlaceholderReading Refused = new(PathPresence.Refused, null);
}

/// <summary>What happened to one request to release a file.</summary>
public enum ReleaseResult
{
    /// <summary>The sync app was asked to release it.</summary>
    Requested,

    /// <summary>It no longer met the rules when it was looked at again, so it was left as it was.</summary>
    NoLongerEligible,

    /// <summary>It is not there any more.</summary>
    Gone,

    /// <summary>Windows would not open it, or would not change its pin.</summary>
    Refused,
}

/// <summary>The outcome of one request, and the bytes it asked for where it asked.</summary>
public readonly record struct ReleaseAnswer(ReleaseResult Result, long RequestedBytes = 0);
