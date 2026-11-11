using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.VirtualDisks;

/// <summary>
/// What the host's file system says about one virtual disk's files.
/// </summary>
/// <param name="Presence">
/// Whether every file is there. A refusal is not an absence, and outranks one: a disk with one file
/// refused and one missing is not known to be missing.
/// </param>
/// <param name="Length">The files' length, which is the most the disk has ever needed. Zero unless present.</param>
/// <param name="OnDisk">
/// The space the files take on the drive, or null where Windows would not say for one of them. Smaller
/// than <paramref name="Length"/> only for a sparse or compressed file.
/// </param>
/// <param name="IsSparse">Whether a file is sparse, which changes the route that makes it smaller.</param>
public sealed record VirtualDiskSize(PathPresence Presence, long Length, long? OnDisk, bool IsSparse)
{
    /// <summary>The figure to report as the space the disk takes on the drive.</summary>
    public long Taken => OnDisk ?? Length;

    /// <summary>
    /// The file <see cref="Presence"/> is about where it is not <see cref="PathPresence.Present"/>: the
    /// part that is missing or refused, which for a split disk need not be the file its record names.
    /// Null where every file was measured.
    /// </summary>
    public string? Unmeasured { get; init; }
}

/// <summary>
/// Measures a virtual disk's files from outside, without opening them.
///
/// <para>A disk a virtual machine is running from is held open by it, so a read that opened the file
/// for its contents would be refused. The two calls here ask the file system about the file rather
/// than open it, and both answer for a file in use.</para>
///
/// <para>Its output is sizes and a flag, so no test can show from its result whether the path it asked
/// with carried the <c>\\?\</c> prefix; it asks with <see cref="LongPath.Extended"/> regardless.</para>
/// </summary>
public static class VirtualDiskFile
{
    /// <summary>
    /// The disk's file and every part its descriptor names, added together. A disk whose descriptor
    /// could not be read is refused at its own file: its parts are not known, so neither is its size.
    /// </summary>
    public static VirtualDiskSize Measure(VirtualDisk disk)
    {
        ArgumentNullException.ThrowIfNull(disk);

        if (disk.Parts is not { } parts)
        {
            return new VirtualDiskSize(PathPresence.Refused, 0, null, false) { Unmeasured = disk.Path };
        }

        var total = Measure(disk.Path);

        foreach (var part in parts)
        {
            if (total.Presence == PathPresence.Refused)
            {
                break;
            }

            var size = Measure(part);

            total = (total.Presence, size.Presence) switch
            {
                (PathPresence.Present, PathPresence.Present) => new VirtualDiskSize(
                    PathPresence.Present,
                    total.Length + size.Length,
                    total.OnDisk + size.OnDisk,
                    total.IsSparse || size.IsSparse),

                // A refused part outranks a missing file found before it, for the reason Presence gives.
                (_, PathPresence.Refused) => size,
                (PathPresence.Present, _) => size,
                _ => total,
            };
        }

        return total;
    }

    public static VirtualDiskSize Measure(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var presence = LongPath.ProbeFile(path);

        if (presence != PathPresence.Present)
        {
            return new VirtualDiskSize(presence, 0, null, false) { Unmeasured = path };
        }

        var extended = LongPath.Extended(path);

        FileInfo file;

        try
        {
            file = new FileInfo(extended);
            file.Refresh();
            _ = file.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Present a moment ago and not describable now: removed in between, or refused. Either
            // way there is no figure, and an absence is the claim that would be false.
            return new VirtualDiskSize(PathPresence.Refused, 0, null, false) { Unmeasured = path };
        }

        return new VirtualDiskSize(
            PathPresence.Present,
            file.Length,
            OccupancyProbe.OnDisk(extended),
            file.Attributes.HasFlag(FileAttributes.SparseFile));
    }
}
