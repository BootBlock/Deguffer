using System.Runtime.InteropServices;

namespace Deguffer.App.Shell;

/// <summary>
/// Whether Windows is in a high contrast theme.
///
/// <c>Windows.UI.ViewManagement.AccessibilitySettings</c> is the obvious API and does not work
/// here: it needs a <c>CoreWindow</c>, which a WinUI 3 desktop app does not have, and throws
/// 0x80070490 the moment you subscribe. The Win32 query has no such dependency.
/// </summary>
internal static class HighContrast
{
    private const uint SpiGetHighContrast = 0x0042;
    private const uint HcfHighContrastOn = 0x00000001;

    public static bool IsEnabled()
    {
        var info = new HighContrastInfo { CbSize = (uint)Marshal.SizeOf<HighContrastInfo>() };

        return SystemParametersInfo(SpiGetHighContrast, info.CbSize, ref info, 0)
            && (info.Flags & HcfHighContrastOn) != 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct HighContrastInfo
    {
        public uint CbSize;
        public uint Flags;
        public nint DefaultScheme;
    }

    // DllImport rather than LibraryImport: the source generator requires AllowUnsafeBlocks across
    // the whole project, which is a large blast radius for one call into user32.
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref HighContrastInfo data, uint winIni);
}
