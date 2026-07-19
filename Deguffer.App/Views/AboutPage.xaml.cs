using System.Reflection;
using Deguffer.App.Shell;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

public sealed partial class AboutPage : Page
{
    public AboutPage() => InitializeComponent();

    /// <summary>
    /// The informational version carries a <c>+sha</c> suffix from the build; the commit is not
    /// what someone reading an about box wants, so only the version itself is shown.
    /// </summary>
    public string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]
        ?? string.Empty;

    /// <summary>
    /// §5.5: recursive enumeration is too slow to be the scanner, so the fast path reads the
    /// volume's file table — which needs administrator rights. Which one is in use is the single
    /// biggest determinant of how long a scan takes.
    /// </summary>
    public string ScanMode { get; } = ElevatedRelaunch.IsElevated
        ? "Running as administrator, so scans read the volume's file table directly. This is the fast path."
        : "Running without administrator rights, so scans walk directories one at a time. Everything works the same, but a first scan is slower — the Storage page offers to restart elevated when that would help.";
}
