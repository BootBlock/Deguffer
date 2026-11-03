using Deguffer.Core.Execution;

namespace Deguffer.Core.Providers;

/// <summary>
/// Whether <c>arduino-cli cache clean</c>, run now, would still remove only the downloads folder the
/// plan examined, holding only what the CLI stages there.
///
/// <para><b>Why the plan's own look is not enough.</b> The command removes whichever folder the CLI
/// resolves as its downloads folder when it starts, outright and with everything in it
/// (<c>commands/service_cache_clean.go</c>). A setting edited after the preview sends it to a folder
/// nobody examined, and a file saved into the folder after the preview goes with the archives. §5.6
/// could notice neither, because what the plan never listed is not something it protects.</para>
///
/// <para>So the step runs only while everything the CLI reads to find the folder is what it was when
/// the CLI answered (see <see cref="ArduinoConfiguration"/>), and the folder, read again by the
/// plan's own rule, is not a link and holds only the CLI's <c>packages</c> and <c>libraries</c>. This
/// asks no subprocess.</para>
///
/// <para><b>And while the machine's own variables are what they were.</b> The plan declined a clean
/// that would reach a folder a CLI the user starts now uses, and that CLI reads the machine's
/// environment, which can change after the preview while Deguffer's own cannot. The CLI binds every
/// <c>ARDUINO_</c> variable to a setting, so any change to one, set, changed or dropped, holds the clean
/// back. So does the machine starting or ceasing to read differently from Deguffer at all: the
/// variables Deguffer started with are not listed here to compare against, so that is read as a
/// change.</para>
/// </summary>
/// <param name="readAgain">Reads the configuration afresh, as the plan first did.</param>
/// <param name="planned">What the CLI read when it answered for the plan.</param>
/// <param name="environmentNow">
/// Reads the machine's environment afresh (<see cref="Safety.IUserEnvironment.ReadChangedEnvironmentNow"/>).
/// </param>
/// <param name="plannedEnvironment">The machine's environment when the plan was made, or null where it read as Deguffer's.</param>
/// <param name="downloads">The downloads folder the plan examined.</param>
internal sealed class ArduinoCleanTarget(
    Func<ArduinoConfiguration> readAgain,
    ArduinoConfiguration planned,
    Func<IReadOnlyDictionary<string, string>?> environmentNow,
    IReadOnlyDictionary<string, string>? plannedEnvironment,
    string downloads) : ICommandTargetCheck
{
    private const string VariablePrefix = "ARDUINO_";

    public string? WhyNot(RunCommandStep step, CancellationToken ct)
    {
        if (!readAgain().SameAs(planned))
        {
            return "the Arduino CLI's configuration, or a setting that moves its folders, has changed since the "
                + $"preview, so its cache clean could remove a folder other than {downloads}";
        }

        if (!SameArduinoVariables(plannedEnvironment, environmentNow()))
        {
            return "a setting that moves Arduino's folders has changed on this machine since the preview, so "
                + $"{downloads} may now be a folder an Arduino CLI you start yourself uses";
        }

        return ArduinoFolderRules.WhyNotEmptied(downloads) switch
        {
            { Absent: true } => $"{downloads} is no longer there",
            { Unlisted: true } => $"Windows would not list {downloads}, so Deguffer cannot tell what the Arduino CLI's "
                + "cache clean would remove",
            { Why: { } why } => $"{downloads} changed since the preview: {LowerFirst(why)}",
            _ => null,
        };
    }

    private static bool SameArduinoVariables(IReadOnlyDictionary<string, string>? then, IReadOnlyDictionary<string, string>? now) =>
        (then, now) switch
        {
            (null, null) => true,
            ({ } before, { } after) => ArduinoVariables(before).SetEquals(ArduinoVariables(after)),
            _ => false,
        };

    /// <summary>Each <c>ARDUINO_</c> variable as one entry, its name in one case because Windows ignores the case of a name.</summary>
    private static HashSet<string> ArduinoVariables(IReadOnlyDictionary<string, string> environment) =>
    [
        .. environment
            .Where(pair => pair.Key.StartsWith(VariablePrefix, StringComparison.OrdinalIgnoreCase))
            .Select(pair => $"{pair.Key.ToUpperInvariant()}={pair.Value}"),
    ];

    /// <summary>The sentence as a clause, which completes "Not run: …".</summary>
    private static string LowerFirst(string sentence) =>
        sentence.Length == 0 ? sentence : char.ToLowerInvariant(sentence[0]) + sentence[1..].TrimEnd('.');
}
