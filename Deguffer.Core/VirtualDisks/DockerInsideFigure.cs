using Deguffer.Core.Safety;

namespace Deguffer.Core.VirtualDisks;

/// <summary>
/// What Docker said about the space inside its data disk, or why it was not asked.
/// </summary>
/// <param name="Usage">Docker's own figures, or null where there are none.</param>
/// <param name="Why">Why there are no figures, for the user. Null where there are.</param>
public sealed record DockerInsideReading(DockerDiskUsage? Usage, string? Why);

/// <summary>
/// Asks the Docker engine for <c>docker system df</c>, and only when Docker Desktop is already running.
///
/// <para>§11 puts starting the engine out of scope: measuring a disk must not start the virtual machine
/// that holds it. So the engine's own process is looked for first, and a Docker Desktop that is not
/// running gives no inside figure and says so. The command is sent to the <c>desktop-linux</c> context
/// by name, which Docker Desktop has created since 3.5, because the context the user last chose can be
/// a remote engine whose figures describe a different machine.</para>
///
/// <para>The command only reads. Nothing here is allowed to run a command that changes Docker's state,
/// and the tests assert the whole set of commands it runs.</para>
/// </summary>
public sealed class DockerInsideFigure(IUserEnvironment environment, IProcessRunner runner, IProcessInspector inspector)
{
    /// <summary>
    /// Docker Desktop's backend, which every Linux container's networking passes through, so it runs
    /// whenever the engine does. <c>com.docker.service</c> is not used, because Docker documents that it
    /// runs from boot whether Docker Desktop is open or not.
    /// </summary>
    public const string EngineProcess = "com.docker.backend";

    public const string Arguments = "--context desktop-linux system df --format \"{{json .}}\"";

    public async Task<DockerInsideReading> ReadAsync(CancellationToken ct)
    {
        if (inspector.FindRunning([EngineProcess]).Count == 0)
        {
            return new(null, "Docker Desktop is not running, and Deguffer does not start it to ask how much of "
                + "the disk is reclaimable inside.");
        }

        if (environment.FindExecutable("docker") is not { } docker)
        {
            return new(null, "Docker Desktop is running, but the docker command is not on the PATH, so Docker "
                + "could not be asked how much of the disk is reclaimable inside.");
        }

        var outcome = await runner.RunAsync(docker, Arguments, ct).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            return new(null, $"Docker did not say how much of the disk is reclaimable inside: {outcome.Message}");
        }

        return DockerDiskUsage.Parse(outcome.StandardOutput) is { } usage
            ? new(usage, null)
            : new(null, "Docker answered in a form Deguffer does not read, so how much of the disk is "
                + "reclaimable inside is not shown.");
    }
}
