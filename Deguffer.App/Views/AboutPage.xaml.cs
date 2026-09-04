using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Deguffer.App.Shell;
using Deguffer.Core.Safety;
using Microsoft.UI.Xaml.Controls;
using RuntimeArchitecture = System.Runtime.InteropServices.Architecture;

namespace Deguffer.App.Views;

public sealed partial class AboutPage : Page
{
    /// <summary>
    /// The key the build stamps the date under; see the AssemblyMetadata item in the project file.
    /// </summary>
    private const string BuildDateKey = "BuildDate";

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
    /// The date the build stamped into the assembly, in the reader's own culture. Nothing in the
    /// compiled output carries it otherwise, so an unstamped assembly says so rather than guessing
    /// from a file timestamp, which a copy or a restore would silently change.
    /// </summary>
    public string BuildDate { get; } = ReadStampedBuildDate();

    /// <summary>
    /// Named as the project's platforms are (<c>x86</c>, <c>x64</c>, <c>ARM64</c>) rather than as
    /// the runtime's enum is, because that is what the download the user chose was called.
    /// </summary>
    public string Architecture { get; } = DisplayName(RuntimeInformation.ProcessArchitecture);

    /// <summary>
    /// What the architecture actually means for the reader. The app ships self-contained per
    /// architecture (§6.3), so this build runs on one processor family and no other, and the
    /// explanation says which and why that matters rather than leaving a bare "x64" on the page.
    /// </summary>
    public string ArchitectureExplanation { get; } =
        BuildArchitectureExplanation(
            RuntimeInformation.ProcessArchitecture,
            RuntimeInformation.OSArchitecture);

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

    /// <summary>
    /// §3's four tiers, in the same words the badge on a Storage row explains itself with. The
    /// page states all four side by side and a badge states one; neither owns the wording, so both
    /// read it from <see cref="SafetyTierExtensions.ToExplanation"/>.
    /// </summary>
    public string RegenerableCacheExplanation { get; } = SafetyTier.RegenerableCache.ToExplanation();

    /// <inheritdoc cref="RegenerableCacheExplanation" />
    public string RegenerableWithCostExplanation { get; } = SafetyTier.RegenerableWithCost.ToExplanation();

    /// <inheritdoc cref="RegenerableCacheExplanation" />
    public string UserDataExplanation { get; } = SafetyTier.UserData.ToExplanation();

    /// <inheritdoc cref="RegenerableCacheExplanation" />
    public string DoNotTouchExplanation { get; } = SafetyTier.DoNotTouch.ToExplanation();

    private static string ReadStampedBuildDate()
    {
        var stamped = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == BuildDateKey)
            ?.Value;

        // Stamped in UTC so the value does not depend on how the build machine's clock is set.
        return DateOnly.TryParseExact(
            stamped,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date)
            ? date.ToString("D", CultureInfo.CurrentCulture)
            : "not known";
    }

    private static string DisplayName(RuntimeArchitecture architecture) =>
        architecture switch
        {
            RuntimeArchitecture.X86 => "x86",
            RuntimeArchitecture.X64 => "x64",
            RuntimeArchitecture.Arm64 => "ARM64",

            // Nothing publishes these, but the runtime enum is wider than the project's platforms
            // and a bare enum name is a better answer than a wrong one.
            _ => architecture.ToString(),
        };

    private static string BuildArchitectureExplanation(
        RuntimeArchitecture process,
        RuntimeArchitecture operatingSystem)
    {
        var built = DisplayName(process);
        var machine = DisplayName(operatingSystem);

        var explanation =
            $"Deguffer is built once for each kind of processor, and this copy is the {built} build. " +
            "Each build carries its own copy of .NET and the Windows App SDK, so nothing has to be " +
            "installed for it to run, and each is made for one processor family.";

        // Windows runs a foreign build regardless, but by two different mechanisms: it emulates
        // x64 and x86 on ARM64, and it runs x86 beside x64 through WOW64, which is not emulation.
        // Only the first is reliably slower, so the sentence names the compatibility layer rather
        // than promising a speed the machine can contradict.
        return process == operatingSystem
            ? $"{explanation} This machine's processor is {machine}, so this is the native build for it."
            : $"{explanation} This machine's processor is {machine}, so the {machine} build is the " +
              "native one here and this one is not. Windows runs this one through a compatibility " +
              "layer that the native build does not need.";
    }
}
