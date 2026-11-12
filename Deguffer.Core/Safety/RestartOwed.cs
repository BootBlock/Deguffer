namespace Deguffer.Core.Safety;

/// <summary>The registry keys whose presence means Windows owes a restart.</summary>
public enum RestartKey
{
    /// <summary>
    /// <c>Component Based Servicing\RebootPending</c>. Also created by the servicing stack for the
    /// length of any DISM session, a read-only analysis included, and removed when the session ends.
    /// </summary>
    ServicingRebootPending,

    /// <summary><c>WindowsUpdate\Auto Update\RebootRequired</c>, which Windows Update writes.</summary>
    WindowsUpdateRebootRequired,

    /// <summary><c>Component Based Servicing\RebootInProgress</c>.</summary>
    ServicingRebootInProgress,

    /// <summary><c>Component Based Servicing\PackagesPending</c>.</summary>
    ServicingPackagesPending,
}

/// <summary>One of the restart keys, present.</summary>
/// <param name="Key">Which key.</param>
/// <param name="Written">
/// When the key was last written, in UTC, from the machine's own clock, or null where it could not be
/// read. A key that refuses to be opened is present with no time.
/// </param>
public sealed record RestartMark(RestartKey Key, DateTimeOffset? Written);

/// <summary>Which of the restart keys were present when Windows was asked.</summary>
/// <param name="Marks">Every key present. None where no restart is owed.</param>
public sealed record RestartOwed(IReadOnlyList<RestartMark> Marks)
{
    /// <summary>No restart owed.</summary>
    public static readonly RestartOwed None = new([]);

    /// <summary>Whether any key was present.</summary>
    public bool IsPending => Marks.Count > 0;
}
