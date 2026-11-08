namespace Deguffer.Core.Safety;

/// <summary>
/// Where a file or folder really is, named from the device of the volume that holds it, such as
/// <c>\Device\HarddiskVolume3\Users\testuser\OneDrive</c>. For comparing locations, never for showing
/// one or opening one: no drive letter or <c>\\?\</c> path reads in this form.
///
/// <para><b>The volume is part of the comparison.</b> A path relative to its volume alone would not
/// prove the place, because a folder turned into a junction to another volume could lead to the same
/// relative path there.</para>
///
/// <para>Two are equal when they name the same place in the way Windows compares names on a volume:
/// without regard to case, and with or without a trailing separator.</para>
/// </summary>
/// <param name="Value">The path, as <c>GetFinalPathNameByHandle</c> gives it with <c>VOLUME_NAME_NT</c>.</param>
public readonly record struct VolumeDevicePath(string Value)
{
    /// <summary>The place <paramref name="relative"/> leads to from this one.</summary>
    /// <param name="relative">A path relative to this one, in display form.</param>
    public VolumeDevicePath Join(string relative) => new(Path.Join(Trimmed, relative));

    public bool Equals(VolumeDevicePath other) =>
        string.Equals(Trimmed, other.Trimmed, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Trimmed);

    private string Trimmed => Value.TrimEnd('\\');
}
