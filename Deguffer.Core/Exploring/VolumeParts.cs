namespace Deguffer.Core.Exploring;

/// <summary>
/// The blocks drawn beside the whole of a scanned volume, in bytes, before any is left out for
/// being too thin to draw. See <see cref="VolumeSpace.Parts"/>.
/// </summary>
/// <param name="ShadowCopies">Restore points and shadow copies, at Windows' figure, or zero.</param>
/// <param name="Reserved">Reserved storage, at Windows' figure, or zero.</param>
/// <param name="Unaccounted">What is in use and neither counted by the scan nor named by Windows.</param>
/// <param name="Free">The volume's free space.</param>
public readonly record struct VolumeParts(long ShadowCopies, long Reserved, long Unaccounted, long Free);
