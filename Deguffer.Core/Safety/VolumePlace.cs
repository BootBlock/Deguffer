namespace Deguffer.Core.Safety;

/// <summary>
/// One path an item is reachable at, and where it sits below the top of its volume when reached
/// that way. See <see cref="VolumeRoot.Places"/>.
/// </summary>
/// <param name="Path">The item's path through one mount point of its volume, in display form.</param>
/// <param name="Readings">
/// Where <paramref name="Path"/> sits below the top of its volume, as
/// <see cref="VolumeRoot.Readings"/> answers it.
/// </param>
public readonly record struct VolumePlace(string Path, IReadOnlyList<string> Readings);
