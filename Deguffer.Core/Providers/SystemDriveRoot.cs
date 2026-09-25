using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// The top of the drive Windows is installed on, as the one declaration any provider reaching into it
/// must use. The counterpart of <see cref="WindowsSystemRoot"/> one level up.
///
/// <para><b>§5.2 is sharpest here.</b> The names an upgrade leaves behind sit beside
/// <c>Program Files</c>, <c>Users</c>, <c>Windows</c> and whatever else somebody keeps at the top of
/// their system drive. The root is never enumerated and never a target: only the names a provider
/// declares are reached, so there is no listing through which an unnamed sibling could be.</para>
///
/// <para><b>What §5.6 asserts is what Windows itself keeps here.</b> An unrecognised sibling cannot be
/// named without listing the root, which is the thing this refuses to do. What can be named is the
/// installation and everything on the machine that depends on it, and those are the siblings an
/// over-broad rule here would do the most damage to.</para>
/// </summary>
public static class SystemDriveRoot
{
    /// <summary>
    /// What Windows keeps at the top of its own drive, every one of which §5.6 has to find standing.
    ///
    /// <para><c>System Volume Information</c> is left out deliberately. Windows refuses even an
    /// administrator a description of it, so asserting it would make every run's verdict "not
    /// checked" and prove nothing about it.</para>
    /// </summary>
    public static readonly IReadOnlyList<(string RelativePath, string Reason)> Survivors =
    [
        ("Windows", "The running Windows installation."),
        ("Program Files", "Every installed program."),
        ("Program Files (x86)", "Every installed 32-bit program."),
        ("ProgramData", "What every program and every account on this machine share."),
        ("Users", "Every account's own files."),
        ("Recovery", "The recovery environment Windows starts when it cannot start itself."),
        ("$Recycle.Bin", "Every account's deleted files on this drive."),
    ];

    /// <summary>The top of the system drive declared with <paramref name="locations"/> under it.</summary>
    public static DeclaredRoot Holding(ISystemDirectories system, params DeclaredLocation[] locations)
    {
        ArgumentNullException.ThrowIfNull(system);

        return new DeclaredRoot(
            system.SystemDrive,
            "The top of the drive Windows is on must survive — it is never listed and never a target, "
            + "and only the names declared on it are removed.",
            RequiresElevation: true,
            locations,
            Survivors);
    }
}
