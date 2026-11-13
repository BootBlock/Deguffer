using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// Delete one explicitly named file.
///
/// Exists for <c>C:\Windows\MEMORY.DMP</c>, which is a single file and the largest single reclaim
/// Deguffer knows about — the size of installed memory after one stop error on a machine configured
/// to write a complete dump. There is no directory to target: the file's parent is the Windows
/// directory itself, which §5.2 forbids touching and this provider never enumerates.
///
/// A file is not a small tree, and the difference is a safety one rather than a convenience. A
/// directory removal walks and can partially succeed; this either removes the one path named or
/// leaves it, so <see cref="FileRemover"/> is a few lines rather than a reuse of
/// <see cref="DirectoryRemover"/> with the walking suppressed.
/// </summary>
public sealed record DeleteFileStep(string Path, string What) : DeleteStep(Path, What)
{
    public override string Description => $"{What} — {LongPath.Display(Path)}";

    /// <summary>
    /// What the guard decided about the file when it was measured, so the plan withdraws a recent
    /// file's step without reading the file again. Null where the measurement judged nothing about
    /// the file's age: it was absent, refused or a mail store, or a scanner gave no verdict. The plan
    /// reads the file for those, and for a verdict given under a guard other than the one in force.
    /// See <see cref="FileAgeVerdict"/>.
    /// </summary>
    public FileAgeVerdict? AgeVerdict { get; init; }
}
