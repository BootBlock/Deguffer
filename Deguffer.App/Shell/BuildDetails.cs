using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using RuntimeArchitecture = System.Runtime.InteropServices.Architecture;

namespace Deguffer.App.Shell;

/// <summary>
/// Which build of Deguffer this is and what it is running on: what About's version card shows, and
/// what Copy details puts on the clipboard for a bug report. Read once, because none of it changes
/// while the process runs.
/// </summary>
/// <param name="Version">The version, without the build's commit suffix.</param>
/// <param name="BuildDate">The date the build stamped into the assembly, or null where it did not.</param>
/// <param name="Process">The processor family this build was made for.</param>
/// <param name="Machine">The processor family Windows itself runs on.</param>
/// <param name="WindowsVersion">The operating system's version number.</param>
/// <param name="IsElevated">Whether this process holds administrator rights, which decides how it scans (§5.5).</param>
public sealed record BuildDetails(
    string Version,
    DateOnly? BuildDate,
    RuntimeArchitecture Process,
    RuntimeArchitecture Machine,
    Version WindowsVersion,
    bool IsElevated)
{
    /// <summary>The key the build stamps the date under; see the AssemblyMetadata item in the project file.</summary>
    private const string BuildDateKey = "BuildDate";

    /// <summary>The first build number of Windows 11, which still reports itself as version 10.0.</summary>
    private const int FirstWindows11Build = 22000;

    /// <summary>This process's details.</summary>
    public static BuildDetails Current { get; } = new(
        AppVersion.Current,
        ReadStampedBuildDate(),
        RuntimeInformation.ProcessArchitecture,
        RuntimeInformation.OSArchitecture,
        Environment.OSVersion.Version,
        ElevatedRelaunch.IsElevated);

    /// <summary>
    /// The build date in the reader's own culture, or "not known" for an unstamped assembly rather
    /// than a guess from a file timestamp, which a copy or a restore would silently change.
    /// </summary>
    public string BuildDateText => BuildDate?.ToString("D", CultureInfo.CurrentCulture) ?? "not known";

    /// <summary>
    /// Named as the project's platforms are (<c>x86</c>, <c>x64</c>, <c>ARM64</c>) rather than as the
    /// runtime's enum is, because that is what the download the user chose was called.
    /// </summary>
    public string ArchitectureText => NameOf(Process);

    /// <summary>The machine's processor family, named as <see cref="ArchitectureText"/> names this build's.</summary>
    public string MachineText => NameOf(Machine);

    /// <summary>
    /// This build's architecture, then the machine's where the two differ, because a build Windows
    /// runs through a compatibility layer is what a slow report most needs to say.
    /// </summary>
    public string ArchitectureSummary => Process == Machine ? ArchitectureText : $"{ArchitectureText}, on an {MachineText} processor";

    /// <summary>Windows and this build's architecture on one line, for a report that names the platform once.</summary>
    public string PlatformText => $"{WindowsText}, {ArchitectureSummary}";

    /// <summary>
    /// Windows by the name a reader knows it by, then its version number. Windows 11 reports itself as
    /// 10.0, so the name comes from the build number.
    /// </summary>
    public string WindowsText =>
        $"Windows {(WindowsVersion.Major == 10 && WindowsVersion.Build >= FirstWindows11Build ? "11" : WindowsVersion.Major.ToString(CultureInfo.InvariantCulture))} " +
        $"({WindowsVersion.Major}.{WindowsVersion.Minor}.{WindowsVersion.Build})";

    /// <summary>How this process measures a location, in the words the version card's Scanning row leads with.</summary>
    public string ScanModeText => IsElevated
        ? "From the volume's file table, as administrator"
        : "By walking each location, without administrator rights";

    /// <summary>
    /// Everything the version card shows, as plain text to paste into a bug report. Nothing in it is a
    /// path, a user name or a machine name, so it can be pasted into a public issue as it stands. The
    /// date is ISO 8601 (the round-trip form) rather than the reader's culture, so every report reads
    /// it the same way whoever pasted it.
    /// </summary>
    public string ToClipboardText() =>
        string.Join(
            Environment.NewLine,
            $"Deguffer {Version}",
            $"Built: {BuildDate?.ToString("O", CultureInfo.InvariantCulture) ?? "not known"}",
            $"Architecture: {ArchitectureSummary}",
            $"Windows: {WindowsText}",
            $"Scanning: {ScanModeText}");

    private static string NameOf(RuntimeArchitecture architecture) =>
        architecture switch
        {
            RuntimeArchitecture.X86 => "x86",
            RuntimeArchitecture.X64 => "x64",
            RuntimeArchitecture.Arm64 => "ARM64",

            // Nothing publishes these, but the runtime enum is wider than the project's platforms
            // and a bare enum name is a better answer than a wrong one.
            _ => architecture.ToString(),
        };

    private static DateOnly? ReadStampedBuildDate()
    {
        var stamped = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == BuildDateKey)
            ?.Value;

        // Stamped in UTC so the value does not depend on how the build machine's clock is set.
        return DateOnly.TryParseExact(stamped, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }
}
