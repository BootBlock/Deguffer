using Deguffer.Core.Safety;

namespace Deguffer.Testing;

/// <summary>
/// System directories named by a test and never created, for a rule that only compares paths
/// against them. <see cref="FakeSystemDirectories"/> builds real ones for a rule that reaches in.
/// </summary>
public sealed record FixedSystemDirectories(
    string WindowsDirectory,
    string ProgramData,
    string ProgramFiles,
    string ProgramFilesX86,
    string SystemDrive) : ISystemDirectories
{
    /// <summary>The layout of a 64-bit Windows installed on <c>C:\</c>.</summary>
    public static FixedSystemDirectories Standard { get; } = new(
        @"C:\Windows", @"C:\ProgramData", @"C:\Program Files", @"C:\Program Files (x86)", @"C:\");
}
