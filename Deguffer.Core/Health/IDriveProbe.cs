using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Health;

/// <summary>
/// The physical disks on this machine, asked as raw bytes, so that a test states a drive the machine
/// running the suite does not have and the real one is read by the same code.
/// </summary>
public interface IDriveProbe
{
    /// <summary>The number of every disk present, ascending, or null where Windows would not list them.</summary>
    IReadOnlyList<int>? PresentDisks();

    /// <summary>A way to ask <paramref name="disk"/> questions, for one reading of it. Blocks on nothing.</summary>
    IDriveDevice Open(int disk);
}

/// <summary>One disk being asked. Used from one thread at a time, and blocks on the device.</summary>
public interface IDriveDevice : IDisposable
{
    /// <summary>What the drive answered, or the Win32 error it failed with.</summary>
    StorageAnswer Ask(DriveQuestion question);
}
