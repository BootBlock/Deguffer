using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Which environment a variable is read from. The two differ once a variable changes after Deguffer
/// started, because <see cref="ProcessRunner"/> hands every tool this process's own block.
/// </summary>
internal enum VariableReading
{
    /// <summary>
    /// The machine as it now stands (<see cref="IUserEnvironment.GetEnvironmentVariable"/>), which a
    /// tool the user starts next reads.
    /// </summary>
    Current,

    /// <summary>
    /// The block a tool Deguffer starts inherits (<see cref="IUserEnvironment.GetToolEnvironmentVariable"/>),
    /// which that tool's own command acts on.
    /// </summary>
    Tool,
}
