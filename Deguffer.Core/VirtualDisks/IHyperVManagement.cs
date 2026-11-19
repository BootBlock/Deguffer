namespace Deguffer.Core.VirtualDisks;

/// <summary>
/// Hyper-V's management service, as WMI's <c>root\virtualization\v2</c> namespace answers for it.
///
/// <para>Behind an interface because the answer is the machines of whoever runs the suite, and a test
/// that reached the real service would register their disks. <see cref="HyperVWmi"/> is the machine's.
/// What the answers mean is decided in <see cref="HyperVMachines"/>, never here, so a recorded answer
/// tests every rule.</para>
/// </summary>
public interface IHyperVManagement
{
    /// <summary>
    /// A session with Hyper-V's namespace, or null with WMI's code in <paramref name="hr"/> where it would
    /// not open. Blocks on a call into a system service in another process.
    /// </summary>
    IHyperVSession? Open(out int hr);
}

/// <summary>One connection to Hyper-V's namespace, asked several things and released once.</summary>
public interface IHyperVSession : IDisposable
{
    /// <summary>
    /// Every <c>Msvm_ComputerSystem</c>: each virtual machine, and the host, which no machine's settings
    /// name. Null with WMI's code in <paramref name="hr"/> where the listing failed.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled before Hyper-V answered.</exception>
    IReadOnlyList<HyperVSystemRow>? Systems(out int hr, CancellationToken ct);

    /// <summary>
    /// Every <c>Msvm_VirtualSystemSettingData</c>: each machine's active settings and each of its
    /// checkpoints, with the storage each associates. Null with WMI's code in <paramref name="hr"/> where
    /// the listing failed.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled before Hyper-V answered.</exception>
    IReadOnlyList<HyperVSettingsRow>? Settings(out int hr, CancellationToken ct);

    /// <summary>
    /// <c>Msvm_ImageManagementService.GetVirtualHardDiskSettingData</c> for the disk at
    /// <paramref name="path"/>, which reads what Hyper-V knows of the file without opening it as a disk,
    /// so a running machine's disk is described too.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled before the call.</exception>
    HyperVDiskAnswer DescribeDisk(string path, CancellationToken ct);
}
