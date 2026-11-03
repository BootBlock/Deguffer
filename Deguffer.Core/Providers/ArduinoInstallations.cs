using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// The Arduino CLIs on this machine and what each says of the configuration it answers for, asked once
/// per planning pass because the presence probe, Explore and the plan all need it.
///
/// <para>Apart from <see cref="ArduinoCacheProvider"/>, which plans from the answers, because finding
/// the CLIs and asking them under each environment is a job of its own, and the clean's check reads the
/// same configuration again from here.</para>
///
/// <para><b>Two environments.</b> A CLI Deguffer starts inherits the environment Deguffer started
/// with. Where the machine's has changed since (<see cref="IUserEnvironment.ChangedEnvironment"/>),
/// each configuration is asked again by a CLI started with the machine's, which is where a CLI the user
/// starts now keeps its folders. The CLI applies its own rules to every variable, so nothing here copies
/// them.</para>
/// </summary>
/// <param name="bundledClis">Where Arduino IDE 2 installs the CLI it bundles.</param>
/// <param name="ideConfigFile">The configuration file Arduino IDE 2 names to its CLI.</param>
/// <param name="defaultData">The data folder where nothing moves it.</param>
internal sealed class ArduinoInstallations(
    IUserEnvironment environment,
    IProcessRunner runner,
    IReadOnlyList<string> bundledClis,
    string ideConfigFile,
    string defaultData)
{
    private readonly PassMemo<Task<ArduinoAnswers>> _answers = new();
    private readonly PassMemo<FoundClis> _clis = new();

    /// <summary>Whether a CLI is on the PATH or installed with Arduino IDE 2.</summary>
    public bool AnyCli => Clis is { Own: not null } or { Ide: not null };

    /// <summary>Every configuration's answers, asked once per planning pass.</summary>
    public Task<ArduinoAnswers> AskAsync(CancellationToken ct) =>
        _answers.Get(async () =>
        {
            List<(string Cli, string? ConfigFile)> asked = [];

            var clis = Clis;

            if ((clis.Own ?? clis.Ide) is { } own)
            {
                asked.Add((own, null));
            }

            // A refusal may hide the file, so it is asked about then too: the CLI says whether it can read it.
            if (LongPath.FileMayExist(ideConfigFile) && (clis.Ide ?? clis.Own) is { } ide)
            {
                asked.Add((ide, ideConfigFile));
            }

            // Null on a machine nobody changed since Deguffer started, where a second answer would be the first.
            var changed = environment.ChangedEnvironment;
            var machine = environment.CurrentEnvironment;
            var installations = new List<ArduinoInstallation>(changed is null ? asked.Count : 2 * asked.Count);

            foreach (var (cli, configFile) in asked)
            {
                installations.Add(await AskAsync(cli, configFile, VariableReading.Tool, ToolRun.Query, ct).ConfigureAwait(false));

                if (changed is not null)
                {
                    installations.Add(await AskAsync(
                        cli, configFile, VariableReading.Current, ToolRun.Query.StartedWith(changed), ct).ConfigureAwait(false));
                }
            }

            return new ArduinoAnswers(installations, machine);
        });

    /// <summary>Every answer is configuration, which the user can change between one scan and the next.</summary>
    public void Forget()
    {
        _answers.Forget();
        _clis.Forget();
    }

    /// <summary>What a CLI started with the environment <paramref name="reading"/> names reads now.</summary>
    public ArduinoConfiguration ReadConfiguration(VariableReading reading, string? configFile) =>
        ArduinoConfiguration.Read(variable => ConfiguredFolder.Read(reading, variable, environment), configFile, defaultData);

    /// <summary>
    /// What a CLI the user starts now reads with the machine's variables as <paramref name="machine"/>
    /// gives them, which the clean's check reads afresh rather than as the planning pass kept them.
    /// </summary>
    public ArduinoConfiguration ReadConfiguration(IReadOnlyDictionary<string, string> machine, string? configFile) =>
        ArduinoConfiguration.Read(variable => machine.GetValueOrDefault(variable), configFile, defaultData);

    /// <summary>
    /// What one CLI says of one configuration, started with the environment <paramref name="reading"/>
    /// names, and what it read to say it.
    /// </summary>
    private async Task<ArduinoInstallation> AskAsync(
        string cli, string? configFile, VariableReading reading, ToolRun run, CancellationToken ct)
    {
        // Read on both sides of the four queries, so the clean is held to what the CLI read when it
        // answered. A configuration that moved while it answered may have given answers from two
        // configurations, and none of them is then the one to hold the clean to.
        var before = ReadConfiguration(reading, configFile);
        var settings = await ArduinoSettings.AskAsync(runner, cli, configFile, run, ct).ConfigureAwait(false);

        return new ArduinoInstallation(
            cli,
            configFile,
            reading,
            before,
            ReadConfiguration(reading, configFile).SameAs(before) || before.WhyUnknown is not null ? settings : null);
    }

    /// <summary>
    /// The Arduino CLI on the PATH, which answers for its own configuration, and the one Arduino IDE 2
    /// installed with itself, which answers for the IDE's, found once per planning pass.
    ///
    /// <para>A bundled CLI Windows would not describe is taken as there: the row is then planned, and a
    /// CLI that cannot be started says so, rather than the row reporting Arduino as not installed on a
    /// refusal.</para>
    /// </summary>
    private FoundClis Clis => _clis.Get(() => new FoundClis(
        environment.FindExecutable("arduino-cli"),
        bundledClis.FirstOrDefault(LongPath.FileMayExist)));

    /// <param name="Own">The CLI on the PATH, or null.</param>
    /// <param name="Ide">The CLI Arduino IDE 2 bundles, or null.</param>
    private sealed record FoundClis(string? Own, string? Ide);
}
