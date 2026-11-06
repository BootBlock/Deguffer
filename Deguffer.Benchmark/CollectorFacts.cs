using System.Runtime;

namespace Deguffer.Benchmark;

/// <summary>
/// Which garbage collector the run had, stated in every result: two results are comparable only
/// under the same one. The app runs the default, the workstation collector with background
/// collections, and an experiment can change it from the environment without a new build.
/// </summary>
/// <param name="Server">Whether it is the server collector, one heap per processor, set by <c>DOTNET_gcServer=1</c>.</param>
/// <param name="Concurrent">Whether full collections run in the background beside the program's threads.</param>
/// <param name="Gen0Size">
/// The generation 0 budget the environment set, as it was given (<c>DOTNET_GCgen0size</c>, in
/// hexadecimal bytes), or null for the collector's own.
/// </param>
internal sealed record CollectorFacts(bool Server, bool Concurrent, string? Gen0Size)
{
    public static CollectorFacts Current { get; } = new(
        GCSettings.IsServerGC,
        GCSettings.LatencyMode is not GCLatencyMode.Batch,
        Environment.GetEnvironmentVariable("DOTNET_GCgen0size") ?? Environment.GetEnvironmentVariable("COMPlus_GCgen0size"));

    public override string ToString() =>
        (Server ? "server" : "workstation")
        + (Concurrent ? ", background collections" : ", no background collections")
        + (Gen0Size is { } size ? $", generation 0 budget {size}" : string.Empty);
}
