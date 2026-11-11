using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.VirtualDisks;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// VirtualBox's machines, their disks, their snapshots' disks and the disks no machine uses, found from
/// VirtualBox's own settings files, and reported with VirtualBox's route and no step.
///
/// <para>Every settings file is written in the test's own folder as VirtualBox's schema lays it out, and
/// nothing is run: the runner throws for any program.</para>
/// </summary>
public sealed class VirtualBoxDiskProviderTests : IDisposable
{
    private const string BaseId = "{11111111-1111-4111-8111-111111111111}";
    private const string DeltaId = "{22222222-2222-4222-8222-222222222222}";
    private const string SpareId = "{33333333-3333-4333-8333-333333333333}";
    private const string GlobalId = "{44444444-4444-4444-8444-444444444444}";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public VirtualBoxDiskProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private VirtualBoxDiskProvider Provider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    private string Home => Path.Combine(_environment.UserProfile, ".VirtualBox");

    private string MachineFolder => Path.Combine(_environment.UserProfile, "VirtualBox VMs", "Dev");

    private void Global(string machines, string media = "", string? home = null)
    {
        var folder = home ?? Home;
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "VirtualBox.xml"), $"""
            <?xml version="1.0"?>
            <VirtualBox xmlns="http://www.virtualbox.org/" version="1.19-windows">
              <Global>
                <MachineRegistry>{machines}</MachineRegistry>
                <MediaRegistry><HardDisks>{media}</HardDisks></MediaRegistry>
              </Global>
            </VirtualBox>
            """);
    }

    /// <summary>
    /// A machine running from a differencing image whose base a snapshot keeps, with a disk it lists and no
    /// longer attaches, and whatever else <paramref name="machine"/> adds.
    /// </summary>
    private string Machine(string attributes = "", string attached = DeltaId, string extra = "")
    {
        Directory.CreateDirectory(Path.Combine(MachineFolder, "Snapshots"));
        var vbox = Path.Combine(MachineFolder, "Dev.vbox");
        File.WriteAllText(vbox, $"""
            <?xml version="1.0"?>
            <VirtualBox xmlns="http://www.virtualbox.org/" version="1.19-windows">
              <Machine uuid="{"{"}aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa{"}"}" name="Dev box" {attributes}>
                <MediaRegistry>
                  <HardDisks>
                    <HardDisk uuid="{BaseId}" location="Dev.vdi" format="VDI" type="Normal">
                      <HardDisk uuid="{DeltaId}" location="Snapshots/{DeltaId}.vdi" format="VDI"/>
                    </HardDisk>
                    <HardDisk uuid="{SpareId}" location="Spare.vdi" format="VDI" type="Normal"/>
                  </HardDisks>
                </MediaRegistry>
                <Snapshot uuid="{"{"}bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb{"}"}" name="Clean install">
                  <StorageControllers><StorageController name="SATA">
                    <AttachedDevice type="HardDisk" port="0" device="0"><Image uuid="{BaseId}"/></AttachedDevice>
                  </StorageController></StorageControllers>
                </Snapshot>
                <StorageControllers><StorageController name="SATA">
                  <AttachedDevice type="HardDisk" port="0" device="0"><Image uuid="{attached}"/></AttachedDevice>
                  <AttachedDevice type="DVD" port="1" device="0"><Image uuid="{"{"}cccccccc-cccc-4ccc-8ccc-cccccccccccc{"}"}"/></AttachedDevice>
                </StorageController></StorageControllers>
                {extra}
              </Machine>
            </VirtualBox>
            """);

        foreach (var disk in (string[])["Dev.vdi", Path.Combine("Snapshots", $"{DeltaId}.vdi"), "Spare.vdi"])
        {
            File.WriteAllBytes(Path.Combine(MachineFolder, disk), new byte[4096]);
        }

        return vbox;
    }

    private static string Text(CleanupPlan plan) => string.Join("\n", plan.Notes.Select(note => note.Message));

    [Fact]
    public async Task NoSettingsMeansNoRow()
    {
        Assert.False(await Provider().IsPresentAsync());
    }

    /// <summary>
    /// The disk the machine attaches is its current disk, the base under it is a snapshot's, and a disk it
    /// lists and does not attach is said to be unused. All are registered, with the .vbox. Mutation: reading
    /// attachments from snapshots as the machine's own makes the base the current disk.
    /// </summary>
    [Fact]
    public async Task EachDiskIsGivenItsPlaceInTheMachine()
    {
        var vbox = Machine();
        Global($"""<MachineEntry uuid="{"{"}aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa{"}"}" src="{vbox}"/>""");

        var inventory = VirtualBoxMachines.Read(_environment);
        var byPath = inventory.Disks.ToDictionary(disk => Path.GetFileName(disk.Path), disk => disk.Whose);

        Assert.Equal("the VirtualBox virtual machine 'Dev box'", byPath[$"{DeltaId}.vdi"]);
        Assert.Equal("a snapshot of the VirtualBox virtual machine 'Dev box'", byPath["Dev.vdi"]);
        Assert.Equal("a disk the VirtualBox virtual machine 'Dev box' lists and does not use", byPath["Spare.vdi"]);
        Assert.Empty(inventory.Problems);
        Assert.Contains(inventory.Storage(), storage => storage.Path == vbox);

        var text = Text(await Provider().PlanAsync());

        Assert.Contains("The machine has 1 snapshot", text, StringComparison.Ordinal);
        Assert.True(
            text.IndexOf("VBoxManage snapshot \"Dev box\" delete", StringComparison.Ordinal)
                < text.IndexOf("--compact", StringComparison.Ordinal),
            "The route deletes snapshots before it compacts.");
        Assert.Contains("VDI images only", text, StringComparison.Ordinal);
        Assert.DoesNotContain("closemedium", text, StringComparison.Ordinal);
    }

    /// <summary>A machine entry relative to the VirtualBox home, and a home VBOX_USER_HOME moves, are both followed.</summary>
    [Fact]
    public void ARelativeMachineEntryIsReadAgainstTheHomeVboxUserHomeNames()
    {
        var home = Path.Combine(_temp.Path, "vbox-home");
        _environment.WithEnvironmentVariable("VBOX_USER_HOME", home);
        var vbox = Machine();
        Global($"""<MachineEntry uuid="{"{"}aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa{"}"}" src="{Path.GetRelativePath(home, vbox)}"/>""", home: home);

        Assert.Equal(3, VirtualBoxMachines.Read(_environment).Disks.Count);
    }

    [Fact]
    public void AVboxUserHomeThatIsNotAFullPathIsSaidRatherThanGuessed()
    {
        _environment.WithEnvironmentVariable("VBOX_USER_HOME", "relative\\home");

        Assert.Contains("VBOX_USER_HOME", Assert.Single(VirtualBoxMachines.Read(_environment).Problems), StringComparison.Ordinal);
    }

    /// <summary>A disk only the global registry lists, which no machine attaches, is reported and registered as unused.</summary>
    [Fact]
    public async Task ADiskOnlyTheGlobalRegistryListsIsReportedAsUnused()
    {
        Directory.CreateDirectory(Home);
        var orphan = Path.Combine(Home, "Old.vdi");
        File.WriteAllBytes(orphan, new byte[4096]);
        Global(string.Empty, $"""<HardDisk uuid="{GlobalId}" location="Old.vdi" format="VDI" type="Normal"/>""");

        var provider = Provider();

        Assert.Contains("a disk registered with VirtualBox that no virtual machine uses", Text(await provider.PlanAsync()), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(await provider.RegisteredStorageAsync(), storage => storage.Path == orphan);
    }

    /// <summary>A disk the global registry lists and a machine attaches, as older VirtualBox kept them, is that machine's.</summary>
    [Fact]
    public void AGlobalDiskAMachineAttachesIsThatMachines()
    {
        var vbox = Machine(attached: GlobalId);
        File.WriteAllBytes(Path.Combine(_environment.UserProfile, "Shared.vdi"), new byte[4096]);
        Global(
            $"""<MachineEntry uuid="{"{"}aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa{"}"}" src="{vbox}"/>""",
            $"""<HardDisk uuid="{GlobalId}" location="..\Shared.vdi" format="VDI" type="Normal"/>""");

        var shared = Assert.Single(VirtualBoxMachines.Read(_environment).Disks, disk => disk.Path.EndsWith("Shared.vdi", StringComparison.Ordinal));

        Assert.Equal(MachineDiskRole.Current, Assert.IsType<VirtualBoxDisk>(shared.Owner).Role);
    }

    [Fact]
    public async Task ASavedMachinesStateIsRegisteredAndTheRouteSaysSo()
    {
        var vbox = Machine(attributes: """stateFile="Snapshots\2026-11-11T10-00-00-000000000Z.sav" """);
        Global($"""<MachineEntry uuid="{"{"}aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa{"}"}" src="{vbox}"/>""");

        var provider = Provider();

        Assert.Contains("The machine is in a saved state", Text(await provider.PlanAsync()), StringComparison.Ordinal);
        Assert.Contains(
            await provider.RegisteredStorageAsync(),
            storage => storage.Path == Path.Combine(MachineFolder, "Snapshots", "2026-11-11T10-00-00-000000000Z.sav"));
    }

    [Fact]
    public async Task AMachineTheRecordsNameThatIsNotThereIsMissingNeverAnOrphan()
    {
        var gone = Path.Combine(_environment.UserProfile, "VirtualBox VMs", "Gone", "Gone.vbox");
        Global($"""<MachineEntry uuid="{"{"}aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa{"}"}" src="{gone}"/>""");

        var missing = Assert.Single((await Provider().PlanAsync()).Notes, note => note.Severity == PlanNoteSeverity.Warning);

        Assert.Contains(gone, missing.Message, StringComparison.Ordinal);
        Assert.Contains("missing", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsWindowsWillNotLetDegufferReadAreSaidAndKeepTheRow()
    {
        Global(string.Empty);
        using var denied = DeniedDirectory.WithUnreadableFile(Path.Combine(Home, "VirtualBox.xml"));

        var provider = Provider();

        Assert.True(await provider.IsPresentAsync());
        Assert.Contains("would not let Deguffer read VirtualBox's list", Text(await provider.PlanAsync()), StringComparison.Ordinal);
    }
}
