using Deguffer.Core.Safety;

namespace Deguffer.Core.Configuration;

/// <summary>
/// One folder the user has approved Deguffer to look for build output in.
/// </summary>
/// <param name="Path">
/// The folder itself, absolute and resolved, in the form <see cref="LongPath.Configured(string?)"/>
/// leaves it.
/// </param>
/// <param name="RemoteStorageApproved">
/// Whether the user was told this folder is on a volume whose contents are not on this machine, and
/// chose it anyway — see <see cref="LocalVolume.StoresContentRemotely"/>.
///
/// <para><b>False is the answer that narrows, and it is the default for every reason a value could be
/// missing.</b> A folder approved before Deguffer read volume flags at all is stored as a bare string
/// and arrives here false; so does a hand-edited entry Deguffer could not make sense of. Discovery
/// then refuses the folder and says so in the plan, which costs the user a search. The other default
/// would spend a download of everything they keep in the cloud on a consent nobody asked them for.
/// </para>
///
/// <para>Stored per folder rather than as one preference for the app. The user is being asked about
/// <em>this</em> folder, whose contents they know; a switch called "search cloud drives" would carry
/// that answer to folders they have not seen.</para>
/// </param>
public sealed record SourceRoot(string Path, bool RemoteStorageApproved = false);

/// <summary>
/// What Deguffer has to tell the user about a folder they have just picked, before it stores it.
///
/// <para>Here rather than on the Settings page for the reason <see cref="Exploring.DriveChoice"/>
/// gives: the sentence is the part worth asserting, and a test can reach Core. The decision behind it
/// is a safety decision as well, and the page must not be the thing that makes it.</para>
/// </summary>
/// <param name="Path">The folder, as the picker returned it.</param>
/// <param name="Warning">
/// What the user has to read before this folder is stored, or null where there is nothing to tell
/// them and the approval is theirs to make in silence.
/// </param>
public sealed record SourceRootApproval(string Path, string? Warning)
{
    /// <summary>
    /// What a folder on a volume whose contents are not on this machine is warned about.
    ///
    /// <para>It names the consequence rather than the mechanism, as
    /// <see cref="Exploring.DriveChoice.RemoteStorageRefusal"/> does, and it states the two halves the
    /// choice turns on. Searching the folder downloads every file in it onto the disk the user is
    /// cleaning, and what Deguffer would then free is space in the cloud rather than space on that
    /// disk — which is §5.4's rule about a virtual disk in a second shape. A warning that said only
    /// "this may be slow" would leave the reader thinking the cost was their time.</para>
    /// </summary>
    public const string RemoteStorageWarning =
        "This folder is on cloud storage shown as a drive letter. Searching it downloads every file "
        + "in it onto this computer, and what Deguffer could then remove is space in the cloud rather "
        + "than space on this disk. Add it only if that is what you want.";

    /// <summary>
    /// What <paramref name="path"/> needs said about it, given what the machine reports about the
    /// volume holding it.
    ///
    /// <para>A path on a volume the inventory has nothing for — a share, or a volume mounted without a
    /// drive letter — is approved in silence. Nothing measured its flags, and warning on no reading
    /// would be a guess (<see cref="HostVolume.For"/>).</para>
    /// </summary>
    public static SourceRootApproval For(IVolumeInventory volumes, string path) =>
        new(
            path,
            HostVolume.For(volumes, path) is { StoresContentRemotely: true } ? RemoteStorageWarning : null);

    /// <summary>Whether the user has to be asked before this folder is stored.</summary>
    public bool NeedsConfirming => Warning is not null;

    /// <summary>
    /// The folder as it is stored, once the user has said yes to whatever <see cref="Warning"/> said.
    ///
    /// <para>The flag follows the warning rather than being passed in, so a folder can only ever
    /// carry the approval by way of the sentence that explains it. A caller that stored a folder with
    /// the flag set and never showed the warning would be recording a consent that was not given, and
    /// there is no overload here that lets it.</para>
    /// </summary>
    public SourceRoot Accepted() => new(Path, RemoteStorageApproved: NeedsConfirming);
}
