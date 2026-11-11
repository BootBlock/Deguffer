using System.Buffers.Binary;
using System.Text;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Exploring.Knowledge;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.VirtualDisks;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// VMware Workstation's machines, their disks and their snapshots' disks, found from Workstation's own
/// records and each disk's descriptor, measured across every file a disk is made of, and reported with
/// Workstation's route and no step.
///
/// <para>Every record is a file in the test's own folder, written as Workstation writes it, and nothing is
/// run: the runner throws for any program.</para>
/// </summary>
public sealed class VmwareDiskProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public VmwareDiskProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private VmwareDiskProvider Provider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    private string Library => Path.Combine(_environment.RoamingAppData, "VMware");

    private void Inventory(params string[] vmx)
    {
        Directory.CreateDirectory(Library);
        var lines = new List<string> { ".encoding = \"UTF-8\"", "vmlist1.config = \"folder1\"", "vmlist1.Type = \"2\"" };
        lines.AddRange(vmx.Select((path, i) => $"vmlist{i + 2}.config = \"{path}\""));
        File.WriteAllLines(Path.Combine(Library, "inventory.vmls"), lines);
    }

    private void Recent(params string[] vmx)
    {
        Directory.CreateDirectory(Library);
        File.WriteAllLines(
            Path.Combine(Library, "preferences.ini"),
            [".encoding = \"UTF-8\"", .. vmx.Select((path, i) => $"pref.mruVM{i}.filename = \"{path}\"")]);
    }

    private string MachineFolder(string name = "Dev")
    {
        var folder = Path.Combine(_environment.Documents!, "Virtual Machines", name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string Vmx(string folder, string name, params string[] lines)
    {
        var path = Path.Combine(folder, $"{name}.vmx");
        File.WriteAllLines(path, [".encoding = \"UTF-8\"", .. lines]);
        return path;
    }

    private static string File_(string folder, string name, int bytes = 4096)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    /// <summary>A text descriptor naming <paramref name="extents"/>, and a parent where there is one.</summary>
    private static string Descriptor(string folder, string name, string? parent, params string[] extents)
    {
        var text = new StringBuilder("# Disk DescriptorFile\nversion=1\nCID=fffffffe\n");
        text.Append(parent is null ? "parentCID=ffffffff\ncreateType=\"twoGbMaxExtentSparse\"\n" : $"parentCID=12345678\ncreateType=\"twoGbMaxExtentSparse\"\nparentFileNameHint=\"{parent}\"\n");
        text.Append("\n# Extent description\n");

        foreach (var extent in extents)
        {
            text.Append($"RW 8323072 SPARSE \"{extent}\"\n");
        }

        text.Append("RW 0 ZERO\n");

        var path = Path.Combine(folder, name);
        File.WriteAllText(path, text.ToString());
        return path;
    }

    private static string Text(CleanupPlan plan) => string.Join("\n", plan.Notes.Select(note => note.Message));

    [Fact]
    public async Task NoRecordsMeansNoRow()
    {
        Assert.False(await Provider().IsPresentAsync());
    }

    /// <summary>
    /// The library and the recently opened list together, each machine once, with a CD image left out and a
    /// display name's escape decoded. Mutation: reading only inventory.vmls loses the second machine.
    /// </summary>
    [Fact]
    public async Task MachinesAreTheLibraryAndTheRecentListTogetherEachOnce()
    {
        var dev = MachineFolder("Dev");
        var devDisk = File_(dev, "Dev.vmdk");
        var devVmx = Vmx(dev, "Dev", "displayName = \"Dev |22box|22\"", "scsi0:0.fileName = \"Dev.vmdk\"",
            "ide1:0.deviceType = \"cdrom-image\"", "ide1:0.fileName = \"C:\\isos\\setup.iso\"");

        var test = MachineFolder("Test");
        var testDisk = File_(test, "Test.vmdk");
        var testVmx = Vmx(test, "Test", "sata0:0.fileName = \"Test.vmdk\"");

        Inventory(devVmx);
        Recent(testVmx, devVmx);

        var inventory = VmwareMachines.Read(_environment);

        Assert.Equal([devDisk, testDisk], inventory.Disks.Select(disk => disk.Path));
        Assert.Equal("the VMware Workstation virtual machine 'Dev \"box\"'", inventory.Disks[0].Whose);
        Assert.Empty(inventory.Problems);
        Assert.Contains(inventory.Storage(), storage => storage.Path == devVmx);
        Assert.True(await Provider().IsPresentAsync());
    }

    /// <summary>The survey's workstation had a recent entry naming a .vmx that no longer existed.</summary>
    [Fact]
    public async Task AMachineTheRecordsNameThatIsNotThereIsMissingNeverAnOrphan()
    {
        var gone = Path.Combine(MachineFolder("Gone"), "Gone.vmx");
        Recent(gone);

        var plan = await Provider().PlanAsync();
        var missing = Assert.Single(plan.Notes, note => note.Severity == PlanNoteSeverity.Warning);

        Assert.Contains(gone, missing.Message, StringComparison.Ordinal);
        Assert.Contains("missing", missing.Message, StringComparison.Ordinal);
        Assert.Contains("nothing in its folder is taken to be left over", missing.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A machine with two snapshots runs from its newest delta, the .vmx names that delta, and each
    /// descriptor's parent hint leads down to the base. Every disk is reported and registered, the deltas
    /// as kept by a snapshot, and the route deletes snapshots before it shrinks.
    /// Mutation: not following parentFileNameHint loses the base disk.
    /// </summary>
    [Fact]
    public async Task ASnapshotChainIsFollowedToItsBaseAndTheRouteDeletesSnapshotsFirst()
    {
        var dev = MachineFolder();
        var base_ = Descriptor(dev, "Dev.vmdk", null, "Dev-s001.vmdk");
        File_(dev, "Dev-s001.vmdk");
        var first = Descriptor(dev, "Dev-000001.vmdk", "Dev.vmdk", "Dev-000001-s001.vmdk");
        File_(dev, "Dev-000001-s001.vmdk");
        var second = Descriptor(dev, "Dev-000002.vmdk", "Dev-000001.vmdk", "Dev-000002-s001.vmdk");
        File_(dev, "Dev-000002-s001.vmdk");
        File.WriteAllLines(Path.Combine(dev, "Dev.vmsd"),
        [
            ".encoding = \"UTF-8\"", "snapshot.numSnapshots = \"2\"",
            "snapshot0.uid = \"1\"", "snapshot0.filename = \"Dev-Snapshot1.vmsn\"", "snapshot0.disk0.fileName = \"Dev.vmdk\"",
            "snapshot1.uid = \"2\"", "snapshot1.filename = \"Dev-Snapshot2.vmsn\"", "snapshot1.disk0.fileName = \"Dev-000001.vmdk\"",
        ]);
        var vmx = Vmx(dev, "Dev", "displayName = \"Dev\"", "nvme0:0.fileName = \"Dev-000002.vmdk\"");
        Inventory(vmx);

        var inventory = VmwareMachines.Read(_environment);

        Assert.Equal([second, first, base_], inventory.Disks.Select(disk => disk.Path));
        Assert.Equal(
            [MachineDiskRole.Current, MachineDiskRole.Checkpoint, MachineDiskRole.Checkpoint],
            inventory.Disks.Select(disk => Assert.IsType<VmwareDisk>(disk.Owner).Role));
        Assert.Contains(inventory.Storage(), storage => storage.Path == Path.Combine(dev, "Dev-Snapshot2.vmsn"));
        Assert.Contains(inventory.Storage(), storage => storage.Path == Path.Combine(dev, "Dev.vmsd"));

        var text = Text(await Provider().PlanAsync());

        Assert.Contains("A snapshot of the VMware Workstation virtual machine 'Dev': " + base_, text, StringComparison.Ordinal);
        Assert.Contains("The machine has 2 snapshots", text, StringComparison.Ordinal);
        Assert.True(
            text.IndexOf("vmrun deleteSnapshot", StringComparison.Ordinal) < text.IndexOf("Clean up Disks", StringComparison.Ordinal),
            "The route deletes snapshots before it shrinks the disk.");
    }

    /// <summary>
    /// A disk split into several files is measured across all of them and every one is registered, since
    /// its descriptor alone is a kilobyte. Mutation: measuring only the descriptor reports its size alone.
    /// </summary>
    [Fact]
    public async Task ASplitDiskIsMeasuredAndRegisteredAcrossEveryExtent()
    {
        var dev = MachineFolder();
        var descriptor = Descriptor(dev, "Dev.vmdk", null, "Dev-s001.vmdk", "Dev-s002.vmdk");
        var first = File_(dev, "Dev-s001.vmdk", 8192);
        var second = File_(dev, "Dev-s002.vmdk", 16384);
        Inventory(Vmx(dev, "Dev", "scsi0:0.fileName = \"Dev.vmdk\""));

        var provider = Provider();
        var text = Text(await provider.PlanAsync());
        var expected = new FileInfo(descriptor).Length + 8192 + 16384;

        Assert.Contains($"1 virtual disk takes {FreeSpace.Format(expected)} on this drive.", text, StringComparison.Ordinal);

        var registered = (await provider.RegisteredStorageAsync()).Select(storage => storage.Path).ToList();
        Assert.Contains(first, registered);
        Assert.Contains(second, registered);
        Assert.Contains(descriptor, registered);
    }

    /// <summary>A missing extent is named as missing, rather than the descriptor that names it.</summary>
    [Fact]
    public async Task AMissingExtentIsNamedAsMissing()
    {
        var dev = MachineFolder();
        Descriptor(dev, "Dev.vmdk", null, "Dev-s001.vmdk", "Dev-s002.vmdk");
        File_(dev, "Dev-s001.vmdk");
        Inventory(Vmx(dev, "Dev", "scsi0:0.fileName = \"Dev.vmdk\""));

        var missing = Assert.Single((await Provider().PlanAsync()).Notes, note => note.Severity == PlanNoteSeverity.Warning);

        Assert.Contains(Path.Combine(dev, "Dev-s002.vmdk"), missing.Message, StringComparison.Ordinal);
        Assert.Contains("missing", missing.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A part Windows will not describe outranks a missing one found before it: the disk may be whole, so it
    /// is said to be unreadable, never missing.
    /// </summary>
    [Fact]
    public async Task ARefusedExtentOutranksAMissingOne()
    {
        var dev = MachineFolder();
        Descriptor(dev, "Dev.vmdk", null, "Dev-s001.vmdk", "Dev-s002.vmdk");
        var refused = File_(dev, "Dev-s002.vmdk");
        Inventory(Vmx(dev, "Dev", "scsi0:0.fileName = \"Dev.vmdk\""));

        using var denied = DeniedDirectory.WithUnreadableFile(refused);

        var warning = Assert.Single((await Provider().PlanAsync()).Notes, note => note.Severity == PlanNoteSeverity.Warning);

        Assert.Contains($"Windows would not let Deguffer read {refused}", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("missing", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A descriptor that will not be read leaves the disk's files unknown, which is said as unknown and
    /// never as missing or as a disk of one file.
    /// </summary>
    [Fact]
    public async Task ADescriptorThatWillNotBeReadLeavesTheSizeUnknownNotMissing()
    {
        var dev = MachineFolder();
        var descriptor = Descriptor(dev, "Dev.vmdk", null, "Dev-s001.vmdk");
        File_(dev, "Dev-s001.vmdk");
        Inventory(Vmx(dev, "Dev", "scsi0:0.fileName = \"Dev.vmdk\""));

        using var denied = DeniedDirectory.WithUnreadableContent(descriptor);

        var text = Text(await Provider().PlanAsync());

        Assert.Contains($"Windows would not let Deguffer read {descriptor}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("missing", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A disk in one sparse file keeps its descriptor inside, at the sector the header names, and a delta's
    /// parent hint there is followed. Mutation: reading the header's offsets at the wrong bytes loses the base.
    /// </summary>
    [Fact]
    public void ASparseDisksEmbeddedDescriptorIsRead()
    {
        var dev = MachineFolder();
        var base_ = File_(dev, "Dev.vmdk");
        var delta = Path.Combine(dev, "Dev-000001.vmdk");
        var descriptor = Encoding.ASCII.GetBytes(
            "# Disk DescriptorFile\nversion=1\ncreateType=\"monolithicSparse\"\nparentFileNameHint=\"Dev.vmdk\"\nRW 41943040 SPARSE \"Dev-000001.vmdk\"\n");
        var file = new byte[1024 + 512];
        "KDMV"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(28), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(36), 2);
        descriptor.CopyTo(file, 512);
        File.WriteAllBytes(delta, file);

        var description = VmdkDescriptor.Read(delta);

        Assert.Equal(base_, description.Parent);
        Assert.Empty(description.Parts!);
    }

    /// <summary>
    /// A descriptor Workstation wrote in Windows-1252 names its extents in that encoding. Read as UTF-8, a
    /// non-ASCII extent would be registered at a path that is not there, and protected as absent; an
    /// encoding Windows does not know leaves the disk's files unknown rather than guessed.
    /// Mutation: decoding as UTF-8 names a file that does not exist.
    /// </summary>
    [Fact]
    public void ADescriptorIsReadInTheEncodingItNames()
    {
        var dev = MachineFolder("Développement");
        var extent = File_(dev, "Développement-s001.vmdk");
        var descriptor = Path.Combine(dev, "Développement.vmdk");
        File.WriteAllBytes(descriptor, Encoding.Latin1.GetBytes(
            "# Disk DescriptorFile\nversion=1\nencoding=\"windows-1252\"\ncreateType=\"twoGbMaxExtentSparse\"\n"
            + "RW 8323072 SPARSE \"Développement-s001.vmdk\"\n"));

        Assert.Equal([extent], VmdkDescriptor.Read(descriptor).Parts);

        File.WriteAllText(descriptor, "# Disk DescriptorFile\nencoding=\"no-such-encoding\"\nRW 8 SPARSE \"x-s001.vmdk\"\n");

        Assert.Null(VmdkDescriptor.Read(descriptor).Parts);
    }

    /// <summary>Explore describes each extent of a split disk, which are the large files it draws, not only the descriptor.</summary>
    [Fact]
    public void ExploreDescribesEveryExtentOfASplitDisk()
    {
        var dev = MachineFolder();
        Descriptor(dev, "Dev.vmdk", null, "Dev-s001.vmdk");
        var extent = File_(dev, "Dev-s001.vmdk", 65536);
        Inventory(Vmx(dev, "Dev", "displayName = \"Dev\"", "scsi0:0.fileName = \"Dev.vmdk\""));

        var item = ItemGuide.For(new FakeSystemDirectories(_temp.Path), _environment, new FakeVolumeInventory()).Describe(extent);

        Assert.NotNull(item);
        Assert.Contains("VMware Workstation virtual machine 'Dev'", item.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASuspendedMachinesStateIsRegisteredAndTheRouteSaysSo()
    {
        var dev = MachineFolder();
        File_(dev, "Dev.vmdk");
        var vmss = File_(dev, "Dev-1a2b.vmss");
        Inventory(Vmx(dev, "Dev", "scsi0:0.fileName = \"Dev.vmdk\"", "checkpoint.vmState = \"Dev-1a2b.vmss\""));

        var provider = Provider();

        Assert.Contains("The machine is suspended", Text(await provider.PlanAsync()), StringComparison.Ordinal);
        Assert.Contains(await provider.RegisteredStorageAsync(), storage => storage.Path == vmss);
    }

    /// <summary>Snapshot records that will not be read are unknown, never none: the route still deletes snapshots first.</summary>
    [Fact]
    public async Task SnapshotRecordsThatWillNotBeReadAreUnknownNeverNone()
    {
        var dev = MachineFolder();
        File_(dev, "Dev.vmdk");
        var vmsd = File_(dev, "Dev.vmsd");
        Inventory(Vmx(dev, "Dev", "scsi0:0.fileName = \"Dev.vmdk\""));

        using var denied = DeniedDirectory.WithUnreadableContent(vmsd);

        var text = Text(await Provider().PlanAsync());

        Assert.Contains("Whether the machine has snapshots could not be read", text, StringComparison.Ordinal);
        Assert.Contains("whether it has snapshots is not known", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALibraryWindowsWillNotLetDegufferReadIsSaidAndKeepsTheRow()
    {
        Inventory();
        using var denied = DeniedDirectory.WithUnreadableFile(Path.Combine(Library, "inventory.vmls"));

        var provider = Provider();

        Assert.True(await provider.IsPresentAsync());
        Assert.Contains("would not let Deguffer read VMware Workstation's library", Text(await provider.PlanAsync()), StringComparison.Ordinal);
    }

    /// <summary>An older library in Windows-1252, as community copies show, is read in that encoding.</summary>
    [Fact]
    public void ARecordInWindows1252IsReadInItsEncoding()
    {
        var bytes = Encoding.Latin1.GetBytes(".encoding = \"windows-1252\"\ndisplayName = \"Caf\u00e9\"\n");

        var entries = VmwareDictionary.Parse(bytes);

        Assert.Equal("Caf\u00e9", entries!["displayname"]);
    }

    [Fact]
    public async Task ExploreDescribesAndRefusesTheDiskAndTheMachinesFiles()
    {
        var dev = MachineFolder();
        var disk = File_(dev, "Dev.vmdk");
        var notes = File_(dev, "notes.txt");
        var vmx = Vmx(dev, "Dev", "displayName = \"Dev\"", "scsi0:0.fileName = \"Dev.vmdk\"");
        Inventory(vmx);

        var system = new FakeSystemDirectories(_temp.Path);
        var item = ItemGuide.For(system, _environment, new FakeVolumeInventory()).Describe(disk);

        Assert.NotNull(item);
        Assert.Contains("VMware Workstation virtual machine 'Dev'", item.Summary, StringComparison.Ordinal);
        Assert.Contains("Clean up Disks", item.Removal, StringComparison.Ordinal);

        var policy = await ExploreActionPolicy.ForAsync(system, _environment, new FakeVolumeInventory(), [Provider()]);

        Assert.False(policy.MayRemove(disk).IsAllowed);
        Assert.False(policy.MayRemove(vmx).IsAllowed);
        Assert.True(policy.MayRemove(notes).IsAllowed);
    }

    /// <summary>
    /// The run-wide refusal for a VMware machine: a row whose location holds one leaves its disk, its
    /// extents and its .vmx, and the run's §5.6 negative names each as having survived.
    /// </summary>
    [Fact]
    public async Task ARowWhoseLocationHoldsAVmwareMachineLeavesEveryFileAndTheRunProvesIt()
    {
        var caches = Path.Combine(_environment.UserProfile, ".gradle", "caches");
        var dev = Path.Combine(caches, "vms", "Dev");
        Directory.CreateDirectory(dev);
        File.WriteAllBytes(Path.Combine(caches, "modules.bin"), new byte[4096]);
        var descriptor = Descriptor(dev, "Dev.vmdk", null, "Dev-s001.vmdk");
        var extent = File_(dev, "Dev-s001.vmdk");
        var vmx = Vmx(dev, "Dev", "scsi0:0.fileName = \"Dev.vmdk\"");
        Inventory(vmx);

        var gradle = new GradleCacheProvider(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);
        Assert.Contains((await gradle.PlanAsync()).TargetedPaths, path => LongPath.Contains(path, extent));

        var report = Provider();
        var planner = new CleanupPlanner([gradle, report], volumes: new FakeVolumeInventory());
        var cache = Assert.Single(await planner.PlanAsync([gradle]));
        var results = await planner.ExecuteAsync([cache]);

        Assert.All(results, result => Assert.True(result.Verification!.Passed, result.Verification.Summary));

        var proof = Assert.Single(results, result => result.ProviderId == report.Id);

        foreach (var file in (string[])[descriptor, extent, vmx])
        {
            Assert.True(File.Exists(file), $"a row whose location held {file} deleted it");
            Assert.Contains(proof.Verification!.Checks, check => check.Subject == file && check.Outcome == VerificationOutcome.Survived);
        }
    }
}
