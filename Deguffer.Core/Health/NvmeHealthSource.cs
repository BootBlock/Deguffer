using Deguffer.Core.Health.Parsing;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Health;

/// <summary>Reads an NVMe drive. Every question here is an ordinary query, which normally needs no elevation.</summary>
internal sealed class NvmeHealthSource : IDriveHealthSource
{
    public bool Reads(StorageBusType bus) => bus is StorageBusType.Nvme;

    public DriveReadout Read(IDriveDevice device, string model)
    {
        NvmeController? controller = device.Ask(DriveQuestion.NvmeController).Bytes is { } identify
            && NvmeController.TryRead(identify, out var read) ? read : null;

        IReadOnlyList<DriveFact> facts = controller is { } known
            ? [new DriveFact("NVMe version", $"{known.Version.Major}.{known.Version.Minor}")]
            : [];

        var answer = device.Ask(DriveQuestion.NvmeHealth);
        NvmeHealthLog? log = null;

        if (answer.Bytes is not { } health || !NvmeHealthLog.TryRead(health, out log) || log is null)
        {
            return new DriveReadout([], HasPrimarySignal: false, facts, TemperatureCelsius: null, MoreWhenElevated: answer.Win32Error == DeviceCalls.AccessDenied);
        }

        var attributes = NvmeAttributeRows.Of(log, controller);

        if (device.Ask(DriveQuestion.NvmeSelfTest).Bytes is { } selfTest && SelfTestLogs.TryReadNvme(selfTest, out var outcome))
        {
            attributes = [.. attributes, AtaAttributeRows.SelfTest(outcome)];
        }

        return new DriveReadout(attributes, HasPrimarySignal: true, facts, HealthText.Celsius(log.TemperatureKelvin), MoreWhenElevated: false);
    }
}
