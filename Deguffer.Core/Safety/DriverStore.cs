using System.Runtime.InteropServices;

namespace Deguffer.Core.Safety;

/// <inheritdoc />
/// <remarks>
/// <para><b>Each folder is asked of SetupAPI.</b> <c>SetupGetInfDriverStoreLocation</c> is the
/// documented question, and it answers without administrator rights. The registry's
/// <c>DriverDatabase</c> names the folders too, and was measured to list 33 of 167 packages to an
/// account that is not elevated.</para>
/// </remarks>
public sealed partial class DriverStore : IDriverStore
{
    /// <summary>Longer than any folder the store names, which are a few dozen characters under it.</summary>
    private const int LocationCapacity = 1024;

    private readonly IProcessRunner _runner;
    private readonly string _pnpUtil;

    public DriverStore(IProcessRunner runner, ISystemDirectories system)
    {
        _runner = runner;
        _pnpUtil = PnpUtil(system);
    }

    /// <summary>Where <c>pnputil</c> is, which has no 32-bit build. See <see cref="NativeSystemTool"/>.</summary>
    public static string PnpUtil(ISystemDirectories system) => NativeSystemTool.In(system, "pnputil.exe");

    public async Task<DriverStoreListing> ListAsync(CancellationToken ct)
    {
        var outcome = await _runner.RunAsync(_pnpUtil, PnpUtilDriverList.Arguments, ct).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            return DriverStoreListing.Failed($"pnputil did not list the driver store ({outcome.Message}).");
        }

        return PnpUtilDriverList.Parse(outcome.StandardOutput, FolderOf) is var (packages, unread)
            ? new DriverStoreListing(packages, unread)
            : DriverStoreListing.Failed(
                "this version of pnputil cannot list the driver store in a form Deguffer can read.");
    }

    public string? FolderOf(string publishedName)
    {
        var buffer = new char[LocationCapacity];

        return SetupGetInfDriverStoreLocation(publishedName, IntPtr.Zero, IntPtr.Zero, buffer, buffer.Length, out var length)
            && length > 1
            && Path.GetDirectoryName(new string(buffer, 0, length - 1)) is { Length: > 0 } folder
                ? folder
                : null;
    }

    [LibraryImport(
        "setupapi.dll",
        EntryPoint = "SetupGetInfDriverStoreLocationW",
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupGetInfDriverStoreLocation(
        string fileName,
        IntPtr alternatePlatformInfo,
        IntPtr localeName,
        [Out] char[] returnBuffer,
        int returnBufferSize,
        out int requiredSize);
}
