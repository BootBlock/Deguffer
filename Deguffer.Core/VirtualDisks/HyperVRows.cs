namespace Deguffer.Core.VirtualDisks;

// What Hyper-V's namespace gives, as it gives it, with nothing decided: which rows are machines,
// which settings are checkpoints and which storage is a disk are HyperVMachines' to say.

/// <summary>One <c>Msvm_ComputerSystem</c>.</summary>
/// <param name="Name">Its <c>Name</c>: a machine's GUID, which its settings' <c>VirtualSystemIdentifier</c> holds.</param>
/// <param name="ElementName">Its display name, as Hyper-V Manager and <c>Get-VM</c> show it.</param>
/// <param name="EnabledState">Its <c>EnabledState</c>, or null where it was not given.</param>
public sealed record HyperVSystemRow(string Name, string? ElementName, int? EnabledState);

/// <summary>One <c>Msvm_VirtualSystemSettingData</c>, with the storage it associates.</summary>
/// <param name="SystemName">Its <c>VirtualSystemIdentifier</c>: "the name of the CIM_ComputerSystem object to which this setting data belongs".</param>
/// <param name="Type">Its <c>VirtualSystemType</c>, such as <c>Microsoft:Hyper-V:System:Realized</c>.</param>
/// <param name="Storage">Each <c>Msvm_StorageAllocationSettingData</c> it associates.</param>
public sealed record HyperVSettingsRow(string? SystemName, string? Type, IReadOnlyList<HyperVStorageRow> Storage);

/// <summary>One <c>Msvm_StorageAllocationSettingData</c>.</summary>
/// <param name="SubType">Its <c>ResourceSubType</c>, such as <c>Microsoft:Hyper-V:Virtual Hard Disk</c>.</param>
/// <param name="HostResource">The first of its <c>HostResource</c>, the only one Hyper-V sets: a disk's file.</param>
public sealed record HyperVStorageRow(string? SubType, string? HostResource);

/// <summary>What <c>GetVirtualHardDiskSettingData</c> gave for one disk.</summary>
/// <param name="Error">WMI's code where the call itself failed, otherwise <c>S_OK</c>.</param>
/// <param name="ReturnValue">The method's return value: 0 is completed, anything else is not an answer.</param>
/// <param name="SettingData">The embedded <c>Msvm_VirtualHardDiskSettingData</c>, as CIM-XML.</param>
public sealed record HyperVDiskAnswer(int Error, int? ReturnValue, string? SettingData)
{
    /// <summary>A call that failed before Hyper-V answered, with WMI's code.</summary>
    public static HyperVDiskAnswer Failed(int hr) => new(hr, null, null);
}
