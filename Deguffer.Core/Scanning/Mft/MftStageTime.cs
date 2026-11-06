namespace Deguffer.Core.Scanning.Mft;

/// <summary>One stage of a read of a file table, and how long it took.</summary>
public readonly record struct MftStageTime(MftBuildStage Stage, TimeSpan Elapsed);
