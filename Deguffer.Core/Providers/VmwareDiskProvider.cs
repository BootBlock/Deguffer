using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.VirtualDisks;

namespace Deguffer.Core.Providers;

/// <summary>
/// The disks and snapshots of VMware Workstation's virtual machines, from Workstation's own records,
/// reported and never acted on (<c>docs/todo/provider-candidates.md</c> §1). What every disk report
/// shares is in <see cref="VirtualDiskReportProvider"/>, and how the records are read is in
/// <see cref="VmwareMachines"/>.
///
/// <para><b>Every file of the machine registered</b>, not only its disks: the <c>.vmx</c>, the snapshot
/// records and saved snapshot state, and a suspended machine's memory. The survey measured a machine
/// whose <c>.vmx</c> named its snapshot's delta disk rather than its base disk, and deleting that delta
/// would leave a machine that does not start and lose every write since the snapshot.</para>
/// </summary>
public sealed class VmwareDiskProvider : VirtualDiskReportProvider
{
    public VmwareDiskProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner)
    {
    }

    public override string Id => "vmware-disks";

    public override string Name => "VMware Workstation virtual machine disks";

    public override ProviderBasis Basis => ProviderBasis.Observed;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.DeveloperTools,
        Application = "VMware Workstation and VMware Player",
        Publisher = "Broadcom",
        Purpose = "Each VMware virtual machine keeps its drives in virtual disk files, often split into many, and each "
            + "snapshot adds a delta disk that the machine then writes to. The files grow as the machine writes and "
            + "never shrink by themselves.",
        Recommendation = "Deguffer reports each disk Workstation's records name, and each snapshot's, with its size on "
            + "the drive, and names Workstation's own route in order: delete snapshots, then Clean up Disks. It never "
            + "deletes or shrinks a disk, because deleting a snapshot's file breaks the machine.",
    };

    protected override Task<VirtualDiskInventory> ReadInventoryAsync(CancellationToken ct) =>
        Task.FromResult(VmwareMachines.Read(Environment));
}
