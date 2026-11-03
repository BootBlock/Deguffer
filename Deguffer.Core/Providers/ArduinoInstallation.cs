namespace Deguffer.Core.Providers;

/// <summary>What one Arduino CLI said of one configuration, and what it read to say it.</summary>
/// <param name="Cli">The executable that answered, and that the clean runs.</param>
/// <param name="Reading">
/// Which environment it was started with: Deguffer's, as the clean is, or the machine's, as a CLI the
/// user starts now is.
/// </param>
/// <param name="Configuration">What the CLI read to find its folders, recorded just before it answered.</param>
/// <param name="Settings">What it said, or null where any answer was missing.</param>
internal sealed record ArduinoInstallation(
    string Cli, VariableReading Reading, ArduinoConfiguration Configuration, ArduinoSettings? Settings);
