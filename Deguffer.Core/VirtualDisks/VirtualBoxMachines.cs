using System.Xml.Linq;
using Deguffer.Core.Safety;

namespace Deguffer.Core.VirtualDisks;

/// <summary>
/// Every VirtualBox virtual machine this account has registered, with its disks, its snapshots' disks
/// and the files the machine is made of, from VirtualBox's own settings files.
///
/// <para><b>The records.</b> <c>VirtualBox.xml</c> in the VirtualBox home, <c>VBOX_USER_HOME</c> or else
/// <c>%USERPROFILE%\.VirtualBox</c>, "includes global configuration options and a list of registered
/// virtual machines" (the manual's technical background), each a <c>MachineEntry</c> whose <c>src</c>
/// names its <c>.vbox</c>, relative to the home where it is not full. A machine's <c>.vbox</c> and the
/// global file each hold a <c>MediaRegistry</c>, whose differencing images are nested in their parents
/// and whose <c>location</c> is relative to the <c>.vbox</c>'s folder or to the home. A machine attaches a
/// disk by its identifier, and so does each snapshot, which keeps its own storage controllers. All of this
/// is from VirtualBox's own source (<c>src/VBox/Main/xml/Settings.cpp</c> and its schema), because the
/// manual says the files are intentionally undocumented.</para>
///
/// <para><b>A disk no machine uses is still reported, and protected.</b> A disk a machine lists after it
/// was detached, or the global registry lists for no machine, may hold data somebody needs.</para>
/// </summary>
public static class VirtualBoxMachines
{
    private static readonly XNamespace Settings = "http://www.virtualbox.org/";

    private const string Unknown = "so its disks, and whether it has snapshots, are not known.";

    /// <summary>One disk a media registry lists.</summary>
    private sealed record Medium(string Id, string Path, string? Parent, string Format, string? ListedBy);

    /// <summary>One machine's settings, read before any disk is given an owner.</summary>
    private sealed record Machine(VirtualMachineRecord Record, IReadOnlySet<string> Attached, IReadOnlySet<string> Kept);

    public static VirtualDiskInventory Read(IUserEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var configured = environment.GetEnvironmentVariable("VBOX_USER_HOME");

        if (!string.IsNullOrWhiteSpace(configured) && LongPath.Configured(configured) is null)
        {
            return new([], [$"VBOX_USER_HOME is set to {configured}, which is not a full path, so where VirtualBox keeps "
                + "its list of virtual machines is not known."]);
        }

        var home = LongPath.Configured(configured) ?? Path.Combine(environment.UserProfile, ".VirtualBox");
        var global = Path.Combine(home, "VirtualBox.xml");

        switch (LongPath.ProbeFile(global))
        {
            case PathPresence.Absent:
                return VirtualDiskInventory.Empty;

            case PathPresence.Refused:
                return new([], [$"Windows would not let Deguffer read VirtualBox's list of virtual machines, {global}, so "
                    + "its machines and their disks are not known."]);
        }

        if (XmlFile.TryLoad(global)?.Root is not { } root)
        {
            return new([], [$"VirtualBox's list of virtual machines, {global}, could not be read, so its machines and "
                + "their disks are not known."]);
        }

        List<string> problems = [];
        List<VirtualMachineStorage> files = [];
        Dictionary<string, Medium> media = new(StringComparer.OrdinalIgnoreCase);
        List<Machine> machines = [];

        AddMedia(media, root.Element(Settings + "Global"), home, listedBy: null);

        foreach (var entry in root.Elements(Settings + "Global").Elements(Settings + "MachineRegistry").Elements(Settings + "MachineEntry"))
        {
            if (RecordedPath.Resolve(home, (string?)entry.Attribute("src")) is { } vbox
                && ReadMachine(vbox, media, files, problems) is { } machine)
            {
                machines.Add(machine);
            }
        }

        return new VirtualDiskInventory(Disks(media, machines), problems) { MachineFiles = files };
    }

    private static Machine? ReadMachine(
        string vbox, Dictionary<string, Medium> media, List<VirtualMachineStorage> files, List<string> problems)
    {
        var fallback = Path.GetFileNameWithoutExtension(vbox);

        switch (LongPath.ProbeFile(vbox))
        {
            case PathPresence.Absent:
                problems.Add($"VirtualBox's records name the virtual machine {vbox}, and there is no file there. The machine "
                    + "is missing, and nothing in its folder is taken to be left over.");
                return null;

            case PathPresence.Refused:
                files.Add(new VirtualMachineStorage(vbox, $"the VirtualBox virtual machine '{fallback}'"));
                problems.Add($"Windows would not let Deguffer read the VirtualBox virtual machine {vbox}, {Unknown}");
                return null;
        }

        var element = XmlFile.TryLoad(vbox)?.Root?.Element(Settings + "Machine");
        var name = (string?)element?.Attribute("name") is { Length: > 0 } named ? named : fallback;
        var whose = $"the VirtualBox virtual machine '{name}'";

        files.Add(new VirtualMachineStorage(vbox, whose));

        if (element is null)
        {
            problems.Add($"The VirtualBox virtual machine {vbox} could not be read, {Unknown}");
            return null;
        }

        var folder = Path.GetDirectoryName(vbox)!;

        AddMedia(media, element, folder, name);

        var snapshots = element.Descendants(Settings + "Snapshot").ToList();

        // A machine's saved memory and each online snapshot's, relative to the .vbox's folder.
        foreach (var state in snapshots.Prepend(element).Select(owner => RecordedPath.Resolve(folder, (string?)owner.Attribute("stateFile"))))
        {
            if (state is not null)
            {
                files.Add(new VirtualMachineStorage(state, whose));
            }
        }

        var record = new VirtualMachineRecord(name, snapshots.Count, element.Attribute("stateFile") is { Value.Length: > 0 });

        return new Machine(record, Attachments(element), snapshots.SelectMany(Attachments).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The identifiers of the disks <paramref name="owner"/>'s own storage controllers attach.</summary>
    private static HashSet<string> Attachments(XElement owner) =>
    [
        .. owner.Elements(Settings + "StorageControllers")
            .Elements(Settings + "StorageController")
            .Elements(Settings + "AttachedDevice")
            .Where(device => (string?)device.Attribute("type") == "HardDisk")
            .Elements(Settings + "Image")
            .Select(image => Id((string?)image.Attribute("uuid")))
            .OfType<string>(),
    ];

    /// <summary>Every disk in <paramref name="owner"/>'s media registry, each nested image a child of the one around it.</summary>
    private static void AddMedia(Dictionary<string, Medium> media, XElement? owner, string folder, string? listedBy)
    {
        foreach (var disk in owner?.Elements(Settings + "MediaRegistry").Elements(Settings + "HardDisks").Elements(Settings + "HardDisk") ?? [])
        {
            AddMedium(media, disk, parent: null, folder, listedBy);
        }
    }

    private static void AddMedium(Dictionary<string, Medium> media, XElement disk, string? parent, string folder, string? listedBy)
    {
        var id = Id((string?)disk.Attribute("uuid"));

        if (id is null)
        {
            return;
        }

        if (RecordedPath.Resolve(folder, (string?)disk.Attribute("location")) is { } path)
        {
            media.TryAdd(id, new Medium(id, path, parent, (string?)disk.Attribute("format") ?? string.Empty, listedBy));
        }

        foreach (var child in disk.Elements(Settings + "HardDisk"))
        {
            AddMedium(media, child, id, folder, listedBy);
        }
    }

    /// <summary>
    /// Each disk once, with its owner: a disk a machine attaches now is that machine's current disk, the
    /// disks it is built on and those a snapshot attaches are kept by a snapshot, and a disk nothing uses
    /// is said to be unused.
    /// </summary>
    private static IReadOnlyList<VirtualDisk> Disks(Dictionary<string, Medium> media, IReadOnlyList<Machine> machines)
    {
        Dictionary<string, VirtualDisk> owned = new(StringComparer.OrdinalIgnoreCase);

        foreach (var machine in machines)
        {
            Claim(owned, media, machine.Attached, new VirtualBoxDisk(machine.Record, MachineDiskRole.Current));
        }

        foreach (var machine in machines)
        {
            Claim(owned, media, machine.Kept, new VirtualBoxDisk(machine.Record, MachineDiskRole.Checkpoint));
        }

        foreach (var medium in media.Values.Where(medium => !owned.ContainsKey(medium.Id)))
        {
            owned[medium.Id] = Disk(medium, new VirtualBoxMedium(medium.ListedBy));
        }

        return [.. owned.Values.DistinctBy(disk => disk.Path, StringComparer.OrdinalIgnoreCase)];
    }

    private static void Claim(
        Dictionary<string, VirtualDisk> owned, Dictionary<string, Medium> media, IEnumerable<string> ids, VirtualBoxDisk owner)
    {
        var kept = owner with { Role = MachineDiskRole.Checkpoint };

        foreach (var id in ids)
        {
            var role = owner;
            string? next = id;

            // Up the chain to the base, each parent kept by a snapshot. The bound is the registry's size, which a
            // chain longer than would be a loop in the records.
            for (var depth = 0; next is not null && depth <= media.Count; depth++)
            {
                if (!media.TryGetValue(next, out var medium) || owned.ContainsKey(next))
                {
                    break;
                }

                owned[next] = Disk(medium, role);
                next = medium.Parent;
                role = kept;
            }
        }
    }

    /// <summary>
    /// The disk, with a VMDK image's extents read from its descriptor as VMware's own are, since VirtualBox
    /// can keep a disk in that format too.
    /// </summary>
    private static VirtualDisk Disk(Medium medium, DiskOwner owner) => new(medium.Path, owner, null)
    {
        Parts = medium.Format.Equals("VMDK", StringComparison.OrdinalIgnoreCase) ? VmdkDescriptor.Read(medium.Path).Parts : [],
    };

    /// <summary>An identifier as VirtualBox writes it, with or without braces, in the one form compared here.</summary>
    private static string? Id(string? value) =>
        Guid.TryParse(value?.Trim().Trim('{', '}'), out var id) ? id.ToString("D") : null;
}
