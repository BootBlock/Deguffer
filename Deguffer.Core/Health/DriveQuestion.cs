namespace Deguffer.Core.Health;

/// <summary>
/// Everything Deguffer can ask a drive. Each member is one read-only request in
/// <see cref="DriveRequests"/>, so there is no way to send a drive anything that is not listed here.
/// </summary>
public enum DriveQuestion
{
    DeviceNumber,
    Device,
    SeekPenalty,
    Trim,
    Alignment,
    Geometry,
    NvmeHealth,
    NvmeController,
    NvmeSelfTest,
    PredictFailure,
    AtaThresholds,
    AtaIdentify,
    AtaErrorLog,
    AtaSelfTestLog,
}
