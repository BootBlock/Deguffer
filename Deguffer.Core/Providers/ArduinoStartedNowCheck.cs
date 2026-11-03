namespace Deguffer.Core.Providers;

/// <summary>
/// Whether an Arduino CLI the user starts now would still read what it read when the plan was made,
/// checked immediately before <c>arduino-cli cache clean</c> runs.
///
/// <para><b>Why the clean is held to it.</b> The plan declined a clean that would reach a folder such a
/// CLI uses (see <see cref="ArduinoFolderRules.WhyNotClearedBeside"/>), and that CLI reads the
/// machine's environment and the configuration file it names, either of which can change after the
/// preview while Deguffer's own environment cannot. A downloads folder that has since become, say, that
/// CLI's sketchbook would be removed with what it now holds.</para>
///
/// <para><b>What is read again.</b> The machine's variables, afresh rather than as the planning pass
/// kept them. The CLI binds every <c>ARDUINO_</c> variable to a setting, so any of them set, changed or
/// dropped holds the clean back. Then each configuration such a CLI answered for, read with those
/// variables, as <see cref="ArduinoConfiguration"/> records it. Where none was asked, because the
/// machine read as Deguffer's own, the file it reads is the one the clean's own check reads. This asks
/// no subprocess.</para>
/// </summary>
/// <param name="machineNow">Reads the machine's variables afresh (<see cref="Safety.IUserEnvironment.ReadEnvironmentNow"/>).</param>
/// <param name="planned">What the CLIs answered, and the machine's variables when they did.</param>
/// <param name="readConfiguration">Reads one configuration with the given variables.</param>
internal sealed class ArduinoStartedNowCheck(
    Func<IReadOnlyDictionary<string, string>> machineNow,
    ArduinoAnswers planned,
    Func<IReadOnlyDictionary<string, string>, string?, ArduinoConfiguration> readConfiguration)
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

        if (planned.StartedNowInstallations.Any(i => !readConfiguration(now, i.ConfigFile).SameAs(i.Configuration)))
        {
            return "the configuration an Arduino CLI you start yourself reads has changed since the preview, so "
                + $"{downloads} may now be one of its folders";
        }

        return null;
    }

    /// <summary>Each <c>ARDUINO_</c> variable as one entry, its name in one case because Windows ignores the case of a name.</summary>
    private static HashSet<string> ArduinoVariables(IReadOnlyDictionary<string, string> environment) =>
    [
        .. environment
            .Where(pair => pair.Key.StartsWith(VariablePrefix, StringComparison.OrdinalIgnoreCase))
            .Select(pair => $"{pair.Key.ToUpperInvariant()}={pair.Value}"),
    ];
}
