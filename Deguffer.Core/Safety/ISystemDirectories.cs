namespace Deguffer.Core.Safety;

/// <summary>
/// The directories Windows itself owns: its installation, and the data every account shares.
///
/// A third seam rather than a member on either existing one, on the test
/// <see cref="IVolumeInventory"/> already applied to itself. <see cref="IUserEnvironment"/> is the
/// signed-in user — their profile, their <c>PATH</c>, their environment — and
/// <see cref="IVolumeInventory"/> is what the machine has mounted. <c>C:\Windows</c> is neither: it
/// belongs to the operating system, is shared by every account on the machine, and would be the
/// same directory if nobody were signed in at all. Describing one type as "the user and the
/// operating system" is G1's own test for two types.
///
/// It exists at all because §5.2 has to be provable here. A rule that reaches into <c>C:\Windows</c>
/// must be shown never to reach <c>WinSxS</c> or <c>Windows\Installer</c>, and that proof has to run
/// on a machine where nobody is allowed to delete anything in either — so the directory a provider
/// is handed must be one a test can build.
/// </summary>
public interface ISystemDirectories
{
    /// <summary><c>%SystemRoot%</c>, ordinarily <c>C:\Windows</c>.</summary>
    string WindowsDirectory { get; }

    /// <summary><c>%PROGRAMDATA%</c>, ordinarily <c>C:\ProgramData</c>.</summary>
    string ProgramData { get; }
}

/// <inheritdoc />
public sealed class SystemDirectories : ISystemDirectories
{
    /// <summary>The one instance the app runs with (G5).</summary>
    public static readonly SystemDirectories Current = new();

    /// <summary>
    /// Read once, and deliberately with no <c>Invalidate</c> to match the other two seams. Both are
    /// fixed when Windows is installed and cannot move while a process is running, so a discard
    /// method would exist only to be symmetrical — and it would be a second thing that can be
    /// forgotten, for a value that never goes stale.
    /// </summary>
    public string WindowsDirectory { get; } = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public string ProgramData { get; } = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
}
