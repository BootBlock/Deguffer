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
    /// §5.5: the fast path reads the volume's file table, which needs administrator rights, and
    /// falls back to walking wherever the table cannot answer.
    ///
    /// <para>Neither sentence promises a quicker scan, and that is deliberate. Elevating buys a
    /// route, not a stopwatch: on a real machine the table declined 13 of 48 measured locations —
    /// each because a record beneath one did not establish its own size — and building the index
    /// for every volume the plan touches cost more than the walks it replaced, so the whole preview
    /// took 28.8 seconds elevated against 15.5 unelevated. Discovery inside the user's own source
    /// folders is the half that did get quicker, 2.7 seconds against 5.3, and the plan note for
    /// that says so where it applies. A promise the machine can contradict is worse than no
    /// promise.</para>
    /// </summary>
    public string ScanMode { get; } = ElevatedRelaunch.IsElevated
        ? "Running as administrator, so Deguffer can read the volume's file table. Locations it accounts for are measured from it instead of being walked; anything it does not account for is walked, and the plan says which happened."
        : "Running without administrator rights, so every location is measured by walking it. The sizes are the same either way — the Storage page offers to restart elevated where reading the file table would reach further.";
}
