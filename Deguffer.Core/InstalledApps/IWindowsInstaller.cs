using System.Runtime.InteropServices;

namespace Deguffer.Core.InstalledApps;

/// <summary>What Windows Installer says about a product code.</summary>
public enum InstallerProductState
{
    /// <summary>Installed for this account, or for every account (<c>INSTALLSTATE_DEFAULT</c>).</summary>
    Installed,

    /// <summary>Advertised: registered to install on first use (<c>INSTALLSTATE_ADVERTISED</c>).</summary>
    Advertised,

    /// <summary>
    /// Installed for a different account (<c>INSTALLSTATE_ABSENT</c>). Windows files such a
    /// product's entry under the machine-wide key and lists it only for the account that installed
    /// it.
    /// </summary>
    OtherAccount,

    /// <summary>Neither advertised nor installed (<c>INSTALLSTATE_UNKNOWN</c>): not registered at all.</summary>
    Unknown,

    /// <summary>Any other answer, including a code Windows Installer rejects. Proves nothing.</summary>
    Unanswered,
}

/// <summary>
/// Windows Installer's own record of what it installed, behind a seam so the stale rule can be
/// proved without a product installed on the test machine.
/// </summary>
public interface IWindowsInstaller
{
    InstallerProductState QueryProductState(Guid productCode);

    /// <summary>
    /// Every patch Windows Installer holds for this account and for the machine. Asked because a
    /// patch code is unknown to <see cref="QueryProductState"/> whether or not the patch is there.
    /// </summary>
    InstallerPatches QueryPatches();
}

/// <summary>The patch codes Windows Installer listed.</summary>
/// <param name="IsComplete">False where it stopped listing with an error, so a code missing here may still be a patch.</param>
public sealed record InstallerPatches(IReadOnlySet<Guid> Codes, bool IsComplete);

/// <inheritdoc />
/// <remarks>
/// <c>MsiQueryProductState</c> reads registration only. Measured unelevated on 2026-09-30, it
/// answered for per-machine and per-user products alike. WMI's <c>Win32_Product</c> is never used:
/// asking it runs a consistency check that repairs products.
/// </remarks>
public sealed partial class WindowsInstaller : IWindowsInstaller
{
    public static WindowsInstaller Default { get; } = new();

    private const int InstallStateDefault = 5;
    private const int InstallStateAbsent = 2;
    private const int InstallStateAdvertised = 1;
    private const int InstallStateUnknown = -1;

    /// <summary>Per-user managed, per-user unmanaged and per-machine (<c>MSIINSTALLCONTEXT_ALL</c>).</summary>
    private const int InstallContextAll = 7;

    /// <summary>Applied, superseded, obsoleted and registered (<c>MSIPATCHSTATE_ALL</c>).</summary>
    private const int PatchStateAll = 15;

    private const int ErrorSuccess = 0;
    private const int ErrorNoMoreItems = 259;

    private const int BracedGuidLength = 38;

    private WindowsInstaller()
    {
    }

    public InstallerProductState QueryProductState(Guid productCode)
    {
        try
        {
            return MsiQueryProductState(productCode.ToString("B").ToUpperInvariant()) switch
            {
                InstallStateDefault => InstallerProductState.Installed,
                InstallStateAdvertised => InstallerProductState.Advertised,
                InstallStateAbsent => InstallerProductState.OtherAccount,
                InstallStateUnknown => InstallerProductState.Unknown,
                _ => InstallerProductState.Unanswered,
            };
        }
        catch (DllNotFoundException)
        {
            // A Windows image without Windows Installer. Nothing it says can be heard, so nothing is
            // proved either way.
            return InstallerProductState.Unanswered;
        }
    }

    /// <remarks>
    /// Asked for this account only (a null user SID). Measured unelevated on 2026-09-30, it listed
    /// this account's and the machine's patches, and refused every account's with
    /// <c>ERROR_ACCESS_DENIED</c>. This account's and the machine's are the ones that matter: another
    /// account's per-user packages register their providers in that account's hive, which is never
    /// read.
    /// </remarks>
    public InstallerPatches QueryPatches()
    {
        var codes = new HashSet<Guid>();
        // A braced code and its terminating null, which is the buffer Windows Installer documents.
        Span<char> patch = stackalloc char[BracedGuidLength + 1];

        try
        {
            for (var index = 0; ; index++)
            {
                var result = MsiEnumPatchesEx(
                    null, null, InstallContextAll, PatchStateAll, index, patch, IntPtr.Zero, out _, IntPtr.Zero, IntPtr.Zero);

                switch (result)
                {
                    case ErrorSuccess when Guid.TryParse(patch[..BracedGuidLength], out var code):
                        codes.Add(code);
                        break;

                    case ErrorNoMoreItems:
                        return new InstallerPatches(codes, IsComplete: true);

                    default:
                        return new InstallerPatches(codes, IsComplete: false);
                }
            }
        }
        catch (DllNotFoundException)
        {
            return new InstallerPatches(codes, IsComplete: false);
        }
    }

    [LibraryImport("msi.dll", EntryPoint = "MsiQueryProductStateW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MsiQueryProductState(string product);

    [LibraryImport("msi.dll", EntryPoint = "MsiEnumPatchesExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MsiEnumPatchesEx(
        string? product,
        string? userSid,
        int context,
        int filter,
        int index,
        Span<char> patchCode,
        IntPtr targetProductCode,
        out int targetContext,
        IntPtr targetUserSid,
        IntPtr targetUserSidLength);
}
