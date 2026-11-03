namespace Deguffer.Core.Providers;

/// <summary>
/// Whether every Arduino CLI the plan heard from would still read what it read when the plan was made,
/// checked immediately before <c>arduino-cli cache clean</c> runs.
///
/// <para><b>Why the clean is held to it.</b> The plan declined a clean whose folder is, holds or lies
/// inside a folder another answer names: another configuration's (see
/// <see cref="ArduinoFolderRules.WhyNotCleared"/> and <see cref="ArduinoCacheProvider"/>), or one a CLI
/// the user starts now uses (see <see cref="ArduinoFolderRules.WhyNotClearedBeside"/>). Each of those
/// answers came from a configuration file and, for a CLI started now, from the machine's environment,
/// and either can change after the preview while Deguffer's own environment cannot. A downloads folder
/// that has since become, say, the sketchbook of one of them would be removed with what it now
/// holds.</para>
///
/// <para><b>What is read again.</b> The machine's variables, afresh rather than as the planning pass
/// kept them. The CLI binds every <c>ARDUINO_</c> variable to a setting, so any of them set, changed or
/// dropped holds the clean back. Then each configuration the plan was made against, read as its CLI
/// reads it, and compared as <see cref="ArduinoConfiguration"/> records it. One the plan could not
/// hold a clean to is left out: its own clean was declined, and nothing about it can be compared. This
/// asks no subprocess.</para>
/// </summary>
/// <param name="machineNow">Reads the machine's variables afresh (<see cref="Safety.IUserEnvironment.ReadEnvironmentNow"/>).</param>
/// <param name="planned">What the CLIs answered, and the machine's variables when they did.</param>
/// <param name="readTool">Reads one configuration as the CLI the clean starts reads it.</param>
/// <param name="readStartedNow">Reads one configuration with the given variables, as a CLI the user starts now reads it.</param>
internal sealed class ArduinoAnswersCheck(
    Func<IReadOnlyDictionary<string, string>> machineNow,
    ArduinoAnswers planned,
    Func<string?, ArduinoConfiguration> readTool,
    Func<IReadOnlyDictionary<string, string>, string?, ArduinoConfiguration> readStartedNow)
{
    private const string VariablePrefix = "ARDUINO_";

    /// <summary>
    /// Why a clean of <paramref name="downloads"/> must not run now, as a clause completing
    /// "Not run: …", or null where it may.
    /// </summary>
    public string? WhyNot(string downloads)
    {
        var now = machineNow();

        if (!ArduinoVariables(planned.Machine).SetEquals(ArduinoVariables(now)))
        {
            return "a setting that moves Arduino's folders has changed on this machine since the preview, so "
                + $"{downloads} may now be a folder an Arduino CLI you start yourself uses";
        }

        if (Comparable(planned.Tool).Any(i => !readTool(i.ConfigFile).SameAs(i.Configuration)))
        {
            return "another Arduino configuration has changed since the preview, so "
                + $"{downloads} may now be one of its folders";
        }

        if (Comparable(planned.StartedNowInstallations).Any(i => !readStartedNow(now, i.ConfigFile).SameAs(i.Configuration)))
        {
            return "the configuration an Arduino CLI you start yourself reads has changed since the preview, so "
                + $"{downloads} may now be one of its folders";
        }

        return null;
    }

    private static IEnumerable<ArduinoInstallation> Comparable(IEnumerable<ArduinoInstallation> installations) =>
        installations.Where(i => i.Configuration.WhyUnknown is null);

    /// <summary>Each <c>ARDUINO_</c> variable as one entry, its name in one case because Windows ignores the case of a name.</summary>
    private static HashSet<string> ArduinoVariables(IReadOnlyDictionary<string, string> environment) =>
    [
        .. environment
            .Where(pair => pair.Key.StartsWith(VariablePrefix, StringComparison.OrdinalIgnoreCase))
            .Select(pair => $"{pair.Key.ToUpperInvariant()}={pair.Value}"),
    ];
}
