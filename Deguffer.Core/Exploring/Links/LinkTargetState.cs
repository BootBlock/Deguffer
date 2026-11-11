namespace Deguffer.Core.Exploring.Links;

/// <summary>
/// Whether what a link points at is there.
///
/// <para>Three answers rather than two, for the reason <c>PathPresence</c> has three: "Windows would
/// not say" is not "nothing is there". Only <see cref="Gone"/> is ever offered for removal.</para>
/// </summary>
public enum LinkTargetState
{
    /// <summary>The target is there.</summary>
    Present,

    /// <summary>
    /// The target is not there, on a fixed local disk that is there and answered. The one answer that
    /// says the link leads nowhere.
    /// </summary>
    Gone,

    /// <summary>
    /// Deguffer cannot tell: the target is on a drive that is not connected, on a removable drive, on a
    /// network share or a mapped network drive, on a volume named only by its GUID that is not
    /// attached, or Windows would not describe it. A removable drive or a share that is connected
    /// is unreachable too, because the one plugged in now need not be the one the link was made on,
    /// and a share can be a different computer's disk under the same name tomorrow.
    /// </summary>
    Unreachable,
}
