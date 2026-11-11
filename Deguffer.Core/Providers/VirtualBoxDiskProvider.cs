using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.VirtualDisks;

namespace Deguffer.Core.Providers;

/// <summary>
/// The disks and snapshots of VirtualBox's virtual machines, from VirtualBox's own settings files,
/// reported and never acted on (<c>docs/todo/provider-candidates.md</c> §1). What every disk report
/// shares is in <see cref="VirtualDiskReportProvider"/>, and how the settings are read is in
/// <see cref="VirtualBoxMachines"/>.
///
/// <para><b>Researched</b>, not measured: no workstation the survey measured on had VirtualBox.</para>
/// </summary>
public sealed class VirtualBoxDiskProvider : VirtualDiskReportProvider
{
    public VirtualBoxDiskProvider(
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

    public override string Id => "virtualbox-disks";

    public override string Name => "VirtualBox virtual machine disks";

    public override ProviderBasis Basis => ProviderBasis.Researched;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.DeveloperTools,
        Application = "Oracle VirtualBox",
        Publisher = "Oracle",
        Purpose = "Each VirtualBox virtual machine keeps its drives in virtual disk images, and each snapshot adds a "
            + "differencing image that the machine then writes to. The images grow as the machine writes and never "
            + "shrink by themselves.",
        Recommendation = "Deguffer reports each disk VirtualBox has registered, each snapshot's, and any no machine uses, "
            + "with its size on the drive, and names VirtualBox's own route in order: delete snapshots, then compact. It "
            + "never deletes or compacts a disk, because deleting a snapshot's image breaks the machine.",
    };

    protected override Task<VirtualDiskInventory> ReadInventoryAsync(CancellationToken ct) =>
        Task.FromResult(VirtualBoxMachines.Read(Environment));
}
