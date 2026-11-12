namespace Deguffer.Core.Safety;

/// <inheritdoc />
public sealed class DriverStore : IDriverStore
{
    private readonly IProcessRunner _runner;
    private readonly IDeviceDriverReader _devices;
    private readonly IDriverPackageFolders _folders;
    private readonly string _pnpUtil;

    public DriverStore(IProcessRunner runner, ISystemDirectories system, IDeviceDriverReader devices, IDriverPackageFolders folders)
    {
        _runner = runner;
        _devices = devices;
        _folders = folders;
        _pnpUtil = PnpUtil(system);
    }

    /// <summary>Where <c>pnputil</c> is, which has no 32-bit build. See <see cref="NativeSystemTool"/>.</summary>
    public static string PnpUtil(ISystemDirectories system) => NativeSystemTool.In(system, "pnputil.exe");

    /// <remarks>
    /// The devices are read while pnputil lists the packages. Where they do not read, or cannot say
    /// whether an extension package is in use, pnputil is asked again to name each package's devices,
    /// so no package is reported unused on a weaker reading than pnputil's own.
    /// </remarks>
    public async Task<DriverStoreListing> ListAsync(CancellationToken ct)
    {
        var listing = _runner.RunAsync(_pnpUtil, PnpUtilDriverList.Arguments, ToolRun.Query, ct);
        var devices = _devices.Read(ct) is { } read ? DeviceDrivers.Of(read) : null;
        var outcome = await listing.ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            return Failed(outcome);
        }

        if (PnpUtilDriverList.Parse(outcome.StandardOutput, FolderOf, name => devices?.Uses(name) == true) is not var (packages, unread))
        {
            return Unreadable;
        }

        if (devices is not null && packages.All(devices.Classifies))
        {
            return new DriverStoreListing(packages, unread);
        }

        outcome = await _runner.RunAsync(_pnpUtil, PnpUtilDriverList.ArgumentsWithDevices, ToolRun.Query, ct).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            return Failed(outcome);
        }

        return PnpUtilDriverList.ParseWithDevices(outcome.StandardOutput, FolderOf) is var (named, unreadNamed)
            ? new DriverStoreListing(named, unreadNamed)
            : Unreadable;
    }

    private static DriverStoreListing Unreadable =>
        DriverStoreListing.Failed("this version of pnputil cannot list the driver store in a form Deguffer can read.");

    private static DriverStoreListing Failed(CommandOutcome outcome) =>
        DriverStoreListing.Failed($"pnputil did not list the driver store ({outcome.Message}).");

    public string? FolderOf(string publishedName) => _folders.FolderOf(publishedName);
}
