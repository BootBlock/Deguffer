namespace Deguffer.Core.Scanning.Media;

/// <summary>The volume a path is stored on, and the storage that volume sits on (<see cref="ScanTuner.PathStorageOf"/>).</summary>
/// <param name="VolumeRoot">
/// The root of the volume that holds the path, as <see cref="Safety.LocalVolume.RootPath"/> names it,
/// or null for a share, which is on no volume of this machine.
/// </param>
/// <param name="Media">What the volume sits on.</param>
public readonly record struct PathStorage(string? VolumeRoot, VolumeMedia Media);
