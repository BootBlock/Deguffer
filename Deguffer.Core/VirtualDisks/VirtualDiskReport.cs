using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.VirtualDisks;

/// <summary>
/// The Storage page's report on the virtual disks: each disk's size on the drive, Docker's figure for
/// what is reclaimable inside where it gave one, and the route, as the plan's notes.
///
/// <para>The two figures are separate notes and never added together (§5.4). The host figure is what
/// the drive gives back once a disk is compacted, and the inside figure is what pruning frees inside
/// the disk. Adding them would describe nothing.</para>
/// </summary>
public static class VirtualDiskReport
{
    /// <param name="measured">Each disk the tools record, with what the file system said about it.</param>
    /// <param name="inside">What Docker said, or null where no Docker data disk was there to ask about.</param>
    /// <param name="problems">Why a tool's record could not be read.</param>
    public static IReadOnlyList<PlanNote> Notes(
        IReadOnlyList<(VirtualDisk Disk, VirtualDiskSize Size)> measured,
        DockerInsideReading? inside,
        IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(measured);
        ArgumentNullException.ThrowIfNull(problems);

        var notes = new List<PlanNote>();
        var present = measured.Where(entry => entry.Size.Presence == PathPresence.Present).ToList();

        if (present.Count > 0)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"{Count(present.Count)} {(present.Count == 1 ? "takes" : "take")} "
                + $"{FreeSpace.Format(present.Sum(entry => entry.Size.Taken))} on this drive. "
                + VirtualDiskRoutes.InsideIsNotOnTheDrive));
        }

        notes.AddRange(measured.Select(entry => Disk(entry.Disk, entry.Size)));

        if (inside is not null)
        {
            notes.Add(Inside(inside));
        }

        notes.AddRange(problems.Select(problem => new PlanNote(PlanNoteSeverity.Warning, problem)));
        notes.Add(new PlanNote(PlanNoteSeverity.Information, VirtualDiskRoutes.ReportOnly));

        return notes;
    }

    private static PlanNote Disk(VirtualDisk disk, VirtualDiskSize size) => size.Presence switch
    {
        PathPresence.Absent => new PlanNote(
            PlanNoteSeverity.Warning,
            $"{Capitalised(disk.Owner)}: its record names {disk.Path}, and there is no file there. The disk is "
            + "missing, rather than empty."),

        PathPresence.Refused => new PlanNote(
            PlanNoteSeverity.Warning,
            $"{Capitalised(disk.Owner)}: Windows would not let Deguffer read {disk.Path}, so its size on the drive "
            + "is not known."),

        _ => new PlanNote(
            PlanNoteSeverity.Information,
            $"{Capitalised(disk.Owner)}: {disk.Path}. {Size(size)} {VirtualDiskRoutes.For(disk, size.IsSparse)}"),
    };

    private static string Size(VirtualDiskSize size) => size switch
    {
        { OnDisk: null } =>
            $"It is {FreeSpace.Format(size.Length)} long, and Windows did not say how much of the drive it takes.",
        { IsSparse: true } =>
            $"It is sparse, and takes {FreeSpace.Format(size.Taken)} on the drive of the "
            + $"{FreeSpace.Format(size.Length)} it can address.",
        _ => $"It takes {FreeSpace.Format(size.Taken)} on the drive.",
    };

    private static PlanNote Inside(DockerInsideReading inside)
    {
        if (inside.Usage is not { } usage)
        {
            return new PlanNote(PlanNoteSeverity.Information, $"Inside Docker's data disk: {inside.Why}");
        }

        var rows = string.Join(", ", usage.Rows.Select(row =>
            $"{FreeSpace.Format(row.Reclaimable)} of {FreeSpace.Format(row.Size)} in {row.Type.ToLowerInvariant()}"));

        // Docker counts a volume no container uses as reclaimable, and a volume is where a container's
        // data is kept rather than a copy of anything.
        var volumes = usage.Rows.Any(row => row.Type == DockerDiskUsage.VolumesType && row.Reclaimable > 0)
            ? " Unused volumes hold data, and are removed only when a prune is told to."
            : string.Empty;

        return new PlanNote(
            PlanNoteSeverity.Information,
            $"Inside Docker's data disk, Docker reports {FreeSpace.Format(usage.Size)} in use, of which "
            + $"{FreeSpace.Format(usage.Reclaimable)} is reclaimable ({rows}). Pruning frees it inside the disk, "
            + "and the drive gets it back only once the disk is compacted." + volumes);
    }

    private static string Count(int disks) => disks == 1 ? "1 virtual disk" : $"{disks} virtual disks";

    private static string Capitalised(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
