using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.VirtualDisks;

namespace Deguffer.Core.Providers;

/// <summary>
/// The disks and checkpoints of Hyper-V's virtual machines, as Hyper-V lists them, reported and never
/// acted on (<c>docs/todo/provider-candidates.md</c> §1). What every disk report shares is in
/// <see cref="VirtualDiskReportProvider"/>, and how Hyper-V is asked is in <see cref="HyperVMachines"/>.
///
/// <para><b>Where Hyper-V answers.</b> Hyper-V answers only an elevated account or a member of Hyper-V
/// Administrators, so for anyone else the row says that whether there are machines is not known, and
/// registers nothing: the disks it cannot see are protected only by the rules every other row keeps.
/// Where Hyper-V answers, its disks are registered and its own configuration and state files are not,
/// because its answer names only the disks.</para>
///
/// <para><b>Researched</b>, not measured: the workstation the survey measured on had Hyper-V, and nothing
/// answered unelevated.</para>
/// </summary>
public sealed class HyperVDiskProvider : VirtualDiskReportProvider
{
    private readonly HyperVMachines _machines;

    public HyperVDiskProvider(
        ISystemDirectories? system = null,
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
        _machines = new HyperVMachines(system ?? SystemDirectories.Current, Environment, Runner);
    }

    public override string Id => "hyper-v-disks";

    public override string Name => "Hyper-V virtual machine disks";

    public override ProviderBasis Basis => ProviderBasis.Researched;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.DeveloperTools,
        Application = "Hyper-V",
        Publisher = "Microsoft",
        Purpose = "Each Hyper-V virtual machine keeps its drives in virtual disk files, and each checkpoint adds a "
            + "disk of its own that the machine then writes to. The files grow as the machine writes and never shrink "
            + "by themselves.",
        Recommendation = "Deguffer reports each disk Hyper-V lists, and each checkpoint's, with its size on the drive, "
            + "and names Hyper-V's own commands in the order to use them: delete checkpoints with Remove-VMCheckpoint, "
            + "then compact with Optimize-VHD. It never deletes or compacts a disk, because deleting a checkpoint's file "
            + "breaks the machine. Hyper-V answers only an elevated Deguffer or a member of Hyper-V Administrators.",
    };

    protected override Task<VirtualDiskInventory> ReadInventoryAsync(CancellationToken ct) => _machines.ReadAsync(ct);
}
