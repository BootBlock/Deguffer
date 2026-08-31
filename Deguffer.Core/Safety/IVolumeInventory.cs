namespace Deguffer.Core.Safety;

/// <summary>
/// One volume the machine has mounted under a drive letter.
/// </summary>
/// <param name="RootPath">Where it is mounted, in <c>D:\</c> form.</param>
/// <param name="Kind">
/// Fixed, removable, network, and so on. Reported rather than filtered, because which kinds a
/// provider may act on is a safety decision belonging to that provider — and a seam that filtered
/// would leave the decision untestable, since no fake could then present the kind being refused.
/// </param>
/// <param name="IsReady">
/// Whether the volume can be read at all. An optical drive with no disc and a card reader with no
/// card are both mounted and both answer no, and reading anything else about them throws.
/// </param>
public readonly record struct LocalVolume(string RootPath, DriveType Kind, bool IsReady);

/// <summary>
/// The machine's volumes, behind an interface so a provider that works per volume is testable
/// against directories we build rather than against whatever drives the developer happens to have.
///
/// Separate from <see cref="IUserEnvironment"/> rather than a member on it: that interface is the
/// signed-in user — their profile directories, their <c>PATH</c>, their environment — and the set
/// of mounted volumes is a fact about the hardware instead. Describing one type as "the user and
/// the disks" is G1's own test for two types.
/// </summary>
public interface IVolumeInventory
{
    IReadOnlyList<LocalVolume> Volumes { get; }

    /// <summary>
    /// Discard the remembered list, so a drive mounted while the app was open is seen on the next
    /// preview. Called at the start of a planning pass, as <see cref="IUserEnvironment.Invalidate"/>
    /// is.
    /// </summary>
    void Invalidate();
}

/// <inheritdoc />
public sealed class VolumeInventory : IVolumeInventory
{
    /// <summary>
    /// The one instance the app runs with (G5). Stateless apart from the list it remembers, and
    /// that list describes the machine rather than any one caller.
    /// </summary>
    public static readonly VolumeInventory Current = new();

    private readonly Lock _gate = new();

    private IReadOnlyList<LocalVolume>? _volumes;

    /// <summary>
    /// Memoised for the life of a planning pass. Enumerating drives probes every mounted device,
    /// which for an optical drive means waiting on the hardware, and a provider asks the same
    /// question from both <c>IsPresentAsync</c> and <c>PlanAsync</c> (G4).
    /// </summary>
    public IReadOnlyList<LocalVolume> Volumes
    {
        get
        {
            lock (_gate)
            {
                return _volumes ??= Read();
            }
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _volumes = null;
        }
    }

    private static IReadOnlyList<LocalVolume> Read()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            // Nothing rather than a partial view, on the same reasoning as ChildDirectories.Under:
            // a caller decides what the machine holds from what it is handed.
            return [];
        }

        // IsReady is the one member that answers for an empty drive instead of throwing, which is
        // why it is read here and everything else about an unready volume is left alone.
        return [.. drives.Select(d => new LocalVolume(d.RootDirectory.FullName, d.DriveType, d.IsReady))];
    }
}
