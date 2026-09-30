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
}

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

    [LibraryImport("msi.dll", EntryPoint = "MsiQueryProductStateW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MsiQueryProductState(string product);
}
