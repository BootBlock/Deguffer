using Deguffer.Core.Safety;

namespace Deguffer.Core.Cloud;

/// <summary>
/// Which placeholders may have their local copies released, decided once and asked twice: when the
/// plan is made, and again of each file through the same handle that unpins it.
///
/// <para><b>Every rule is Deguffer's, because the API enforces none of them.</b> A scratch sync root
/// showed <c>CfSetPinState</c> unpinning a file with unsynced local edits, a file the user had pinned,
/// and an ordinary file that was no placeholder at all, each without complaint.</para>
/// </summary>
public static class ReleaseRules
{
    /// <summary>
    /// Why <paramref name="file"/> is left as it is, or null where it may be released, given the pin it
    /// inherits from the nearest folder above it that has one.
    ///
    /// <list type="bullet">
    /// <item><b>In sync, with nothing modified.</b> Microsoft documents <c>ModifiedDataSize</c> as bytes
    /// "not in sync with the cloud", and releasing a file that holds some is how edits get lost.</item>
    /// <item><b>Holding something on this PC.</b> A file already online-only has nothing to give back.</item>
    /// <item><b>Not pinned, by itself or by a folder above it.</b> Unpinning collapses "Always keep on this
    /// device" into online-only, and the only way back is a full download. A pinned folder also re-pins
    /// its children whenever Windows re-evaluates inheritance, so releasing one of them would not last.</item>
    /// <item><b>Not already unpinned, and not excluded.</b> The first has been asked already. The second
    /// is outside sync, so the cloud may not hold it.</item>
    /// <item><b>Not changed inside the user's guard window</b>, which every other location honours too.</item>
    /// </list>
    /// </summary>
    public static HeldBack? Hold(Placeholder file, PinState inherited, MinimumAge keep) => file switch
    {
        { OnDiskBytes: <= 0 } => HeldBack.NothingOnDisk,
        { Pin: PinState.Unpinned } => HeldBack.AlreadyRequested,
        { Pin: PinState.Excluded } => HeldBack.Excluded,
        { InSync: false } or { ModifiedBytes: > 0 } => HeldBack.NotInSync,
        _ when file.Pin == PinState.Pinned || inherited == PinState.Pinned => HeldBack.Pinned,
        _ when inherited == PinState.Excluded => HeldBack.Excluded,
        _ when keep.Protects(file.NewestFileTime) => HeldBack.Recent,
        _ => null,
    };

    /// <summary>
    /// The pin a folder passes to what is inside it: its own where it has one, and otherwise what it
    /// inherited. An unpinned folder passes nothing on, because "not needed here" asked of a folder
    /// says nothing about whether each file inside it may go.
    /// </summary>
    public static PinState PassedOn(PinState own, PinState inherited) => own switch
    {
        PinState.Pinned or PinState.Excluded => own,
        _ => inherited,
    };
}

/// <summary>Why a placeholder with something on this PC was left as it is.</summary>
public enum HeldBack
{
    /// <summary>Online-only already. Not reported: there is nothing to release.</summary>
    NothingOnDisk,

    /// <summary>Marked as not needed on this PC before this scan.</summary>
    AlreadyRequested,

    /// <summary>Pinned, or inside a pinned folder.</summary>
    Pinned,

    /// <summary>Holds edits the cloud does not have yet.</summary>
    NotInSync,

    /// <summary>Excluded from sync.</summary>
    Excluded,

    /// <summary>Changed inside the user's guard window.</summary>
    Recent,
}
