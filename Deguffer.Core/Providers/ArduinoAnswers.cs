using Deguffer.Core.Execution;

namespace Deguffer.Core.Providers;

/// <summary>
/// Every answer the Arduino CLIs gave in one planning pass: one per configuration from a CLI started
/// as the clean is, and, where the machine's environment changed since Deguffer started, one more per
/// configuration from a CLI started as one the user starts now would be.
/// </summary>
/// <param name="All">Each answer, in the order the configurations were asked.</param>
/// <param name="Changed">
/// The environment the second answers were asked with (<see cref="Safety.IUserEnvironment.ChangedEnvironment"/>),
/// or null where nothing changed and none was asked. The clean is held to it.
/// </param>
internal sealed record ArduinoAnswers(IReadOnlyList<ArduinoInstallation> All, IReadOnlyDictionary<string, string>? Changed)
{
    /// <summary>The answers of the CLI the clean starts, from which the plan runs and measures.</summary>
    public IReadOnlyList<ArduinoInstallation> Tool { get; } = [.. All.Where(i => i.Reading is VariableReading.Tool)];

    /// <summary>What a CLI the user starts now said, where it said it.</summary>
    public IReadOnlyList<ArduinoSettings> StartedNow { get; } =
        [.. Distinct(All.Where(i => i.Reading is VariableReading.Current))];

    /// <summary>
    /// Whether a CLI the user starts now did not say for one configuration, so nothing can be held
    /// against that one and every clean is declined.
    /// </summary>
    public bool StartedNowUnknown => All.Any(i => i.Reading is VariableReading.Current && i.Settings is null);

    /// <summary>
    /// Every answer once, from either CLI: where a change on the machine moved nothing of Arduino's, a
    /// CLI the user starts now answers as the one Deguffer starts.
    /// </summary>
    public IEnumerable<ArduinoSettings> Answered => Distinct(All);

    /// <summary>
    /// Whether a clean of <paramref name="folder"/> would reach a folder a CLI started now uses, by
    /// <paramref name="rule"/>, adding the rule's reason to <paramref name="notes"/>. Where that CLI
    /// did not say, it would, and the plan's warning says why once.
    /// </summary>
    public bool ReachesStartedNow(string folder, Func<string, ArduinoSettings, string?> rule, List<PlanNote> notes)
    {
        if (StartedNowUnknown)
        {
            return true;
        }

        if (StartedNow.Select(settings => rule(folder, settings)).FirstOrDefault(why => why is not null) is not { } why)
        {
            return false;
        }

        notes.Add(new PlanNote(PlanNoteSeverity.Information, why));
        return true;
    }

    private static IEnumerable<ArduinoSettings> Distinct(IEnumerable<ArduinoInstallation> installations) =>
        installations.Select(i => i.Settings).OfType<ArduinoSettings>().Distinct();
}
