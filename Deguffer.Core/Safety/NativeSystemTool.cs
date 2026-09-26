namespace Deguffer.Core.Safety;

/// <summary>
/// Where a tool Windows ships in <c>System32</c> is, as this process can start it.
///
/// <para>A 32-bit process on 64-bit Windows is redirected from <c>System32</c> to <c>SysWOW64</c>, so
/// it asks through <c>Sysnative</c> instead. The redirection is silent and harmful either way: a tool
/// with no 32-bit build is simply not there, and one with a 32-bit build, such as DISM, refuses to
/// service the running 64-bit Windows.</para>
/// </summary>
public static class NativeSystemTool
{
    public static string In(ISystemDirectories system, string fileName) => Path.Combine(
        system.WindowsDirectory,
        Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? "Sysnative" : "System32",
        fileName);
}
