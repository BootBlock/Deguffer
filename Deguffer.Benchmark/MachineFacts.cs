using System.Runtime.InteropServices;

namespace Deguffer.Benchmark;

/// <summary>
/// What a reader needs to know about the machine to compare two results, and nothing that names it.
/// </summary>
internal sealed record MachineFacts(
    int Processors,
    Version Windows,
    Version Runtime,
    Architecture Architecture,
    bool Elevated,
    bool DebugBuild)
{
    public static MachineFacts Current { get; } = new(
        Environment.ProcessorCount,
        Environment.OSVersion.Version,
        Environment.Version,
        RuntimeInformation.ProcessArchitecture,
        Environment.IsPrivilegedProcess,
        DebugBuild:
#if DEBUG
            true
#else
            false
#endif
        );
}
