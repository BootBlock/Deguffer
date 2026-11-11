namespace Deguffer.Core.VirtualDisks;

/// <summary>
/// One virtual disk a tool on this machine has registered or configured, found from that tool's own
/// records and never by searching for <c>.vhdx</c>, <c>.vmdk</c> or <c>.vdi</c> files.
///
/// <para>A file with the right extension is not evidence of anything: a disk somebody copied aside, a
/// Hyper-V machine's disk and a WSL distribution's disk look alike, and only the tool's own record
/// says which tool would be harmed and which route applies.</para>
/// </summary>
/// <param name="Path">The disk file, in display form, whether or not it is there.</param>
/// <param name="Owner">The tool whose disk it is, which decides its route.</param>
/// <param name="Distribution">
/// The WSL distribution the disk belongs to, as WSL names it, or null for a disk no distribution owns,
/// which is Docker's own data disk from 4.30, its Hyper-V disk, and every virtual machine's disk.
/// </param>
public sealed record VirtualDisk(string Path, DiskOwner Owner, string? Distribution)
{
    /// <summary>
    /// The files that hold the disk's data beside <see cref="Path"/>, as the disk's own descriptor names
    /// them: the extents of a VMware disk split into several files, whose <see cref="Path"/> is a text
    /// descriptor of a kilobyte. Empty for a disk that is one file. Null where the descriptor could not
    /// be read, so what the disk takes on the drive is not known.
    /// </summary>
    public IReadOnlyList<string>? Parts { get; init; } = [];

    /// <summary>Whose the disk is, as a reader knows it: the distribution, or the tool.</summary>
    public string Whose => Owner.Name(this);

    /// <summary>
    /// The disk as storage no clean may remove: the file its record names and every part, each as its
    /// own entry. See <see cref="VirtualMachineStorage"/>.
    /// </summary>
    public IEnumerable<VirtualMachineStorage> AsStorage()
    {
        yield return new VirtualMachineStorage(Path, Whose);

        foreach (var part in Parts ?? [])
        {
            yield return new VirtualMachineStorage(part, Whose);
        }
    }
}
