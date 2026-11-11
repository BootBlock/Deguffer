using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.VirtualDisks;

/// <summary>
/// Every VMware Workstation virtual machine this account's Workstation knows, with its disks, its
/// snapshots' disks and the files the machine is made of, from Workstation's own records.
///
/// <para><b>No single complete list.</b> <c>%APPDATA%\VMware\inventory.vmls</c> is Workstation's library,
/// each machine a <c>vmlistN.config</c> naming its <c>.vmx</c>, beside folder entries whose
/// <c>config</c> is <c>folderN</c>. <c>preferences.ini</c> holds the recently opened list,
/// <c>pref.mruVMN.filename</c>, which is VMware Player's library. Neither is documented, and the
/// survey's workstation had a recent entry naming a <c>.vmx</c> the library did not hold and that no
/// longer existed. So the machines are the union of both, and a <c>.vmx</c> that is not there is said to
/// be missing, never taken to leave an orphaned folder.</para>
///
/// <para><b>Disks from the machine, and from its snapshots.</b> The <c>.vmx</c> names each disk the
/// machine runs from (<c>scsiN:N.fileName</c> and its kin), which is a snapshot's delta disk once there is
/// a snapshot. The <c>.vmsd</c> beside it names each snapshot's disk, "the current virtual disk filename
/// (CAUTION: This is not the child filename)" (Broadcom KB 337269), and each disk's descriptor names its
/// parent, which reaches a linked clone's parent in another machine's folder too.</para>
/// </summary>
public static partial class VmwareMachines
{
    /// <summary>A chain longer than this is a loop in the records, not snapshots.</summary>
    private const int MaximumChain = 256;

    private const string Unknown = "so whether it has snapshots, and every file it is made of, is not known.";

    public static VirtualDiskInventory Read(IUserEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var folder = Path.Combine(environment.RoamingAppData, "VMware");
        List<string> problems = [];
        List<string> machines = [];

        Add(machines, problems, Path.Combine(folder, "inventory.vmls"), "library", LibraryEntry());
        Add(machines, problems, Path.Combine(folder, "preferences.ini"), "recently opened list", RecentEntry());

        List<VirtualDisk> disks = [];
        List<VirtualMachineStorage> files = [];

        foreach (var vmx in machines)
        {
            ReadMachine(vmx, disks, files, problems);
        }

        return new VirtualDiskInventory(disks, problems) { MachineFiles = files };
    }

    /// <summary>Each <c>.vmx</c> one of Workstation's lists names, once.</summary>
    private static void Add(List<string> machines, List<string> problems, string path, string list, Regex key)
    {
        switch (LongPath.ProbeFile(path))
        {
            case PathPresence.Absent:
                return;

            case PathPresence.Refused:
                problems.Add($"Windows would not let Deguffer read VMware Workstation's {list}, {path}, so the virtual "
                    + "machines it names are not listed here.");
                return;
        }

        if (VmwareDictionary.Read(path) is not { } entries)
        {
            problems.Add($"VMware Workstation's {list}, {path}, could not be read, so the virtual machines it names are "
                + "not listed here.");
            return;
        }

        foreach (var (name, value) in entries)
        {
            if (key.IsMatch(name)
                && value.EndsWith(".vmx", StringComparison.OrdinalIgnoreCase)
                && LongPath.Configured(value) is { } vmx
                && !machines.Contains(vmx, StringComparer.OrdinalIgnoreCase))
            {
                machines.Add(vmx);
            }
        }
    }

    private static void ReadMachine(string vmx, List<VirtualDisk> disks, List<VirtualMachineStorage> files, List<string> problems)
    {
        var fallback = Path.GetFileNameWithoutExtension(vmx);

        switch (LongPath.ProbeFile(vmx))
        {
            case PathPresence.Absent:
                problems.Add($"VMware Workstation's records name the virtual machine {vmx}, and there is no file there. "
                    + "The machine is missing, and nothing in its folder is taken to be left over.");
                return;

            case PathPresence.Refused:
                files.Add(new VirtualMachineStorage(vmx, $"the VMware Workstation virtual machine '{fallback}'"));
                problems.Add($"Windows would not let Deguffer read the VMware Workstation virtual machine {vmx}, {Unknown}");
                return;
        }

        var folder = Path.GetDirectoryName(vmx)!;
        var configuration = VmwareDictionary.Read(vmx);
        var name = configuration?.GetValueOrDefault("displayName") is { Length: > 0 } shown ? shown : fallback;
        var whose = $"the VMware Workstation virtual machine '{name}'";

        files.Add(new VirtualMachineStorage(vmx, whose));

        if (configuration is null)
        {
            problems.Add($"The VMware Workstation virtual machine {vmx} could not be read, {Unknown}");
            return;
        }

        // Snapshot state goes to the working directory, which a machine can move (Broadcom KB 319674).
        var working = RecordedPath.Resolve(folder, configuration.GetValueOrDefault("workingDir")) ?? folder;
        var snapshots = ReadSnapshots(Path.ChangeExtension(vmx, ".vmsd"), folder, working, whose, files);

        if (snapshots.Count is null)
        {
            problems.Add($"The snapshot records of the VMware Workstation virtual machine {vmx} could not be read, so "
                + "whether it has snapshots is not known.");
        }

        var suspended = RecordedPath.Resolve(working, configuration.GetValueOrDefault("checkpoint.vmState"));

        foreach (var key in (string[])["nvram", "extendedConfigFile"])
        {
            if (RecordedPath.Resolve(folder, configuration.GetValueOrDefault(key)) is { } file)
            {
                files.Add(new VirtualMachineStorage(file, whose));
            }
        }

        if (suspended is not null)
        {
            files.Add(new VirtualMachineStorage(suspended, whose));
        }

        var machine = new VirtualMachineRecord(name, snapshots.Count, suspended is not null);

        var current = configuration
            .Where(entry => DiskKey().IsMatch(entry.Key) && entry.Value.EndsWith(".vmdk", StringComparison.OrdinalIgnoreCase))
            .Select(entry => RecordedPath.Resolve(folder, entry.Value))
            .OfType<string>();

        AddChain(disks, current, new VmwareDisk(machine, MachineDiskRole.Current));
        AddChain(disks, snapshots.Disks, new VmwareDisk(machine, MachineDiskRole.Checkpoint));
    }

    /// <summary>
    /// How many snapshots the <c>.vmsd</c> records, null where it could not be read, and each snapshot's
    /// disk. A machine that has never had a snapshot may have no <c>.vmsd</c> at all, which is none.
    /// </summary>
    private static (int? Count, IReadOnlyList<string> Disks) ReadSnapshots(
        string vmsd, string folder, string working, string whose, List<VirtualMachineStorage> files)
    {
        switch (LongPath.ProbeFile(vmsd))
        {
            case PathPresence.Absent:
                return (0, []);

            case PathPresence.Refused:
                files.Add(new VirtualMachineStorage(vmsd, whose));
                return (null, []);
        }

        files.Add(new VirtualMachineStorage(vmsd, whose));

        if (VmwareDictionary.Read(vmsd) is not { } entries)
        {
            return (null, []);
        }

        List<string> disks = [];

        foreach (var (key, value) in entries)
        {
            if (SnapshotDiskKey().IsMatch(key) && RecordedPath.Resolve(folder, value) is { } disk)
            {
                disks.Add(disk);
            }
            else if (SnapshotStateKey().IsMatch(key) && RecordedPath.Resolve(working, value) is { } state)
            {
                files.Add(new VirtualMachineStorage(state, whose));
            }
        }

        var count = int.TryParse(entries.GetValueOrDefault("snapshot.numSnapshots"), out var n) && n >= 0
            ? n
            : entries.Keys.Count(key => SnapshotIdKey().IsMatch(key));

        return (count, disks);
    }

    /// <summary>
    /// Each disk not already listed, with every disk its descriptor's parent hints lead to, each of which a
    /// snapshot keeps.
    /// </summary>
    private static void AddChain(List<VirtualDisk> disks, IEnumerable<string> paths, VmwareDisk owner)
    {
        var parents = owner with { Role = MachineDiskRole.Checkpoint };

        foreach (var path in paths)
        {
            string? next = path;
            var role = owner;

            for (var depth = 0; next is not null && depth < MaximumChain; depth++)
            {
                if (disks.Any(known => known.Path.Equals(next, StringComparison.OrdinalIgnoreCase)))
                {
                    break;
                }

                var description = VmdkDescriptor.Read(next);
                disks.Add(new VirtualDisk(next, role, null) { Parts = description.Parts });

                next = description.Parent;
                role = parents;
            }
        }
    }

    [GeneratedRegex(@"^vmlist\d+\.config$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LibraryEntry();

    [GeneratedRegex(@"^pref\.mruVM\d+\.filename$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RecentEntry();

    [GeneratedRegex(@"^(?:scsi|sata|nvme|ide)\d+:\d+\.fileName$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DiskKey();

    [GeneratedRegex(@"^snapshot\d+\.disk\d+\.fileName$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SnapshotDiskKey();

    [GeneratedRegex(@"^snapshot\d+\.filename$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SnapshotStateKey();

    [GeneratedRegex(@"^snapshot\d+\.uid$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SnapshotIdKey();
}
