namespace Deguffer.Core.Health.Parsing;

/// <summary>The sector sizes a drive reports, in bytes. A drive counts its logical blocks in the logical size.</summary>
internal readonly record struct SectorSizes(uint Logical, uint Physical);
