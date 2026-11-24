using Deguffer.App.Shell;
using Deguffer.Core.Viewing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

public sealed partial class AboutPage : Page
{
    /// <summary>
    /// Whether the mark has flown in already while the app has been running. It flies once: the page
    /// is built again on every visit, and a flourish played every time is one the reader has to wait
    /// through every time.
    /// </summary>
    private static bool _markLanded;

    public AboutPage()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            if (_markLanded)
            {
                return;
            }

            _markLanded = true;
            MarkFlight.Play(Mark, Field, SystemMotion.Current.For(MotionToken.Mark));
        };
    }

    /// <summary>Which build this is and what it runs on, which the version card shows and Copy details copies.</summary>
    public BuildDetails Details => BuildDetails.Current;

    /// <summary>
    /// What the architecture actually means for the reader. The app ships self-contained per
    /// architecture (§6.3), so this build runs on one processor family and no other, and the
    /// explanation says which and why that matters rather than leaving a bare "x64" on the page.
    /// </summary>
    public string ArchitectureExplanation { get; } = BuildArchitectureExplanation(BuildDetails.Current);

    /// <summary>
    /// What the Scanning row's value means, under it. §5.5: the fast path reads the volume's file
    /// table, which needs administrator rights, and falls back to walking wherever the table cannot
    /// answer.
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
    public string ScanModeExplanation { get; } = BuildDetails.Current.IsElevated
        ? "Locations the table accounts for are measured from it instead of being walked. Anything it does not account for is walked, and the plan says which happened."
        : "The sizes are the same either way. The Storage, Explore and Duplicates pages each offer to restart as administrator, and Installed apps offers it for entries that apply to all users.";

    /// <inheritdoc cref="AboutStatements.Summary" />
    public string Summary => AboutStatements.Summary;

    /// <inheritdoc cref="AboutStatements.PurposesHeading" />
    public string PurposesHeading => AboutStatements.PurposesHeading;

    /// <inheritdoc cref="AboutStatements.Purposes" />
    public IReadOnlyList<PageStatement> Purposes => AboutStatements.Purposes;

    /// <inheritdoc cref="AboutStatements.TiersIntroduction" />
    public string TiersIntroduction => AboutStatements.TiersIntroduction;

    /// <inheritdoc cref="AboutStatements.Tiers" />
    public IReadOnlyList<TierStatement> Tiers => AboutStatements.Tiers;

    /// <inheritdoc cref="AboutStatements.SafeguardsHeading" />
    public string SafeguardsHeading => AboutStatements.SafeguardsHeading;

    /// <inheritdoc cref="AboutStatements.Safeguards" />
    public IReadOnlyList<PageStatement> Safeguards => AboutStatements.Safeguards;

    /// <inheritdoc cref="ThirdPartyNotices.Components" />
    public IReadOnlyList<ThirdPartyComponent> Components => ThirdPartyNotices.Components;

    /// <summary>
    /// Puts the version card's details on the clipboard, says so beside the button, and announces it,
    /// because a screen reader otherwise hears nothing happen when the button is pressed.
    /// </summary>
    private async void OnCopyDetails(object sender, RoutedEventArgs e)
    {
        var said = await ClipboardText.PutAsync(Details.ToClipboardText(), CancellationToken.None) ?? "Copied. Paste it into your report.";

        CopyDetailsResult.Text = said;
        FrameworkElementAutomationPeer.FromElement(CopyDetailsButton)?.RaiseNotificationEvent(
            AutomationNotificationKind.ActionCompleted,
            AutomationNotificationProcessing.ImportantMostRecent,
            said,
            "CopyDetails");
    }

    private static string BuildArchitectureExplanation(BuildDetails details)
    {
        var built = details.ArchitectureText;
        var machine = details.MachineText;

        var explanation =
            $"Deguffer is built once for each kind of processor, and this copy is the {built} build. " +
            "Each build carries its own copy of .NET and the Windows App SDK, so nothing has to be " +
            "installed for it to run, and each is made for one processor family.";

        // Windows runs a foreign build regardless, but by two different mechanisms: it emulates
        // x64 and x86 on ARM64, and it runs x86 beside x64 through WOW64, which is not emulation.
        // Only the first is reliably slower, so the sentence names the compatibility layer rather
        // than promising a speed the machine can contradict.
        return details.Process == details.Machine
            ? $"{explanation} This machine's processor is {machine}, so this is the native build for it."
            : $"{explanation} This machine's processor is {machine}, so the {machine} build is the " +
              "native one here and this one is not. Windows runs this one through a compatibility " +
              "layer that the native build does not need.";
    }
}
