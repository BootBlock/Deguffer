using System.Globalization;
using Deguffer.Core.Safety;
using Deguffer.Core.Wmi;

namespace Deguffer.Core.VirtualDisks;

/// <summary>
/// Every Hyper-V virtual machine's disks, as Hyper-V itself lists them, read in process from its WMI
/// namespace (<see cref="IHyperVManagement"/>).
///
/// <para><b>Hyper-V's answer, never its default folder.</b> Microsoft says the host's
/// <c>VirtualHardDiskPath</c> "is not a functional setting… the Hyper-V API… and <c>New-VHD</c> don't use
/// it" (the <c>Set-VMHost</c> page), so a disk can be anywhere, and an unattached <c>.vhdx</c> in that
/// folder can be WSL's, Docker's or another host's. Each machine's active settings name the disks it is
/// attached to, each checkpoint's settings name the disks it keeps, and each disk is asked what it is
/// built on (<see cref="HyperVDiskChains"/>), so the whole chain of a machine with checkpoints is named
/// and registered. That is what <c>Get-VMHardDiskDrive</c>, <c>Get-VMSnapshot</c> and <c>Get-VHD</c>
/// read, and the answer keeps each thing they gave: the machine's name, whether it is saved, how many
/// checkpoints it has, its attached disks and the disks its checkpoints and chains keep.</para>
///
/// <para><b>Hyper-V's answer is only an answer to an account it trusts.</b> Every Hyper-V cmdlet refused an
/// account that is neither elevated nor in Hyper-V Administrators where the survey measured it, and a
/// third-party write-up (Altaro, <c>altaro.com/hyper-v/?p=21601</c>) reports <c>Get-VM</c> returning no
/// machines at all, rather than failing, for such an account. So this process's own token is asked first
/// whether it is either (<see cref="IUserEnvironment.IsHyperVAdministrator"/>), and an account that is not
/// is told that whether it has machines is not known, never that it has none, with Hyper-V never asked.</para>
/// </summary>
public sealed class HyperVMachines(ISystemDirectories system, IUserEnvironment environment, IHyperVManagement hyperV)
{
    /// <summary>A machine's own settings, as against a planned machine's or a checkpoint's.</summary>
    private const string RealizedSystem = "Microsoft:Hyper-V:System:Realized";

    /// <summary>
    /// Every kind of checkpoint: standard, recovery, planned, missing and the replica points.
    /// <c>Get-VMSnapshot</c> with no <c>-SnapshotType</c> "gets all checkpoints", and a disk any of them keeps
    /// is one Hyper-V needs.
    /// </summary>
    private const string CheckpointPrefix = "Microsoft:Hyper-V:Snapshot:";

    /// <summary>
    /// A virtual hard disk, as against a DVD or floppy image, which <c>Get-VMHardDiskDrive</c> does not
    /// list and which is the user's file rather than the machine's disk.
    /// </summary>
    private const string VirtualHardDisk = "Microsoft:Hyper-V:Virtual Hard Disk";

    /// <summary>
    /// <c>EnabledState</c> 6, "Enabled but Offline", which the Hyper-V module's <c>VMState</c> calls
    /// <c>Saved</c>, and 32769, "Suspended", which the <c>Msvm_ComputerSystem</c> page gives for a machine
    /// that is suspended. Either keeps the machine's memory on the drive.
    /// </summary>
    private static readonly int[] SavedStates = [6, 32769];

    private const string Unanswered =
        "so whether this account has Hyper-V virtual machines, and how much of the drive their disks take, is not known.";

    /// <summary>
    /// Hyper-V's Virtual Machine Management service, whose WMI provider the namespace is. It is there only
    /// where Hyper-V itself is installed, which a machine with only Hyper-V's management tools is not.
    /// </summary>
    public string Service { get; } = NativeSystemTool.In(system, "vmms.exe");

    /// <summary>
    /// Each machine's disks, or why Hyper-V would not say. Hyper-V is not asked where it is not installed,
    /// or for an account it would refuse, and a service Windows will not describe is asked anyway,
    /// because a refusal is not an absence. The service comes first, so a machine without Hyper-V has no
    /// row for any account.
    ///
    /// <para>The calls block on Hyper-V's service, so they run off the caller's thread, and a cancelled
    /// reading stops waiting for them at once rather than when the service answers.</para>
    /// </summary>
    public async Task<VirtualDiskInventory> ReadAsync(CancellationToken ct)
    {
        if (LongPath.ProbeFile(Service) == PathPresence.Absent)
        {
            return VirtualDiskInventory.Empty;
        }

        if (!environment.IsHyperVAdministrator)
        {
            return Problem(
                "Hyper-V answers only an elevated PowerShell or a member of the Hyper-V Administrators group, and "
                + $"Deguffer is running as neither, {Unanswered}");
        }

        return await Task.Run(() => Read(ct), ct).WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The disks Hyper-V's namespace names, in one session.</summary>
    internal VirtualDiskInventory Read(CancellationToken ct)
    {
        using var session = hyperV.Open(out var hr);

        if (session is null)
        {
            return Problem(WhyNot(hr));
        }

        if (session.Systems(out hr, ct) is not { } systems || session.Settings(out hr, ct) is not { } settings)
        {
            return Problem(WhyNot(hr));
        }

        return Read(systems, settings, new HyperVDiskChains(session, ct));
    }

    /// <summary>
    /// Each machine, in name order as <c>Get-VM</c> lists them: a system whose active settings are a
    /// realized machine's. The host and a planned machine have none, so neither is listed.
    /// </summary>
    private static VirtualDiskInventory Read(
        IReadOnlyList<HyperVSystemRow> systems, IReadOnlyList<HyperVSettingsRow> settings, HyperVDiskChains chains)
    {
        var bySystem = settings
            .Where(setting => setting.SystemName is not null)
            .ToLookup(setting => setting.SystemName!, StringComparer.OrdinalIgnoreCase);

        List<VirtualDisk> disks = [];
        List<string> problems = [];

        foreach (var system in systems
            .Where(system => system.ElementName is { Length: > 0 })
            .OrderBy(system => system.ElementName, StringComparer.OrdinalIgnoreCase))
        {
            var own = bySystem[system.Name];

            if (own.FirstOrDefault(setting => setting.Type == RealizedSystem) is not { } active)
            {
                continue;
            }

            var checkpoints = own
                .Where(setting => setting.Type?.StartsWith(CheckpointPrefix, StringComparison.Ordinal) == true)
                .ToList();
            var attached = Disks(active).ToList();
            List<string> kept = [.. checkpoints.SelectMany(Disks)];
            List<string> unfollowed = [];

            // A copy, because the walk adds each parent it finds to the disks the checkpoints keep.
            List<string> asked = [.. attached, .. kept];
            chains.Follow(asked, kept, unfollowed);

            var name = system.ElementName!;
            var machine = new VirtualMachineRecord(
                name,
                checkpoints.Count,
                system.EnabledState is { } state && SavedStates.Contains(state));

            Add(disks, attached, new HyperVDisk(machine, MachineDiskRole.Current));
            Add(disks, kept, new HyperVDisk(machine, MachineDiskRole.Checkpoint));

            if (unfollowed.FirstOrDefault() is { } first)
            {
                problems.Add(
                    $"Hyper-V would not describe {first}, a disk of the virtual machine '{name}', so the disks it is "
                    + "built on are not listed here.");
            }
        }

        return new VirtualDiskInventory(disks, problems);
    }

    /// <summary>Each virtual hard disk the settings name, as a full path. Anything else is not a disk's file.</summary>
    private static IEnumerable<string> Disks(HyperVSettingsRow settings) =>
        settings.Storage
            .Where(storage => storage.SubType == VirtualHardDisk)
            .Select(storage => LongPath.Configured(storage.HostResource))
            .OfType<string>();

    /// <summary>
    /// Each path not already listed, so a disk attached now and kept by a checkpoint too is named once,
    /// as the disk the machine runs from.
    /// </summary>
    private static void Add(List<VirtualDisk> disks, IEnumerable<string> paths, HyperVDisk owner)
    {
        foreach (var path in paths)
        {
            if (!disks.Any(known => known.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                disks.Add(new VirtualDisk(path, owner, null));
            }
        }
    }

    private static string WhyNot(int hr) =>
        hr == WbemConnection.Unfinished
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Hyper-V did not list its virtual machines within {ToolRun.QueryLimit.TotalMinutes:0} minutes, {Unanswered}")
            : WbemConnection.IsAccessDenied(hr)
                ? $"Hyper-V refused to list its virtual machines to this account, {Unanswered}"
                : string.Create(CultureInfo.InvariantCulture, $"Hyper-V would not list its virtual machines (error 0x{hr:X8}), {Unanswered}");

    private static VirtualDiskInventory Problem(string problem) => new([], [problem]);
}
