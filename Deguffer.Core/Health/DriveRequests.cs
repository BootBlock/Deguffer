using System.Collections.Frozen;
using System.Buffers.Binary;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Health;

/// <summary>Which rights a request needs on the device handle.</summary>
internal enum DriveAccess
{
    /// <summary>No rights: a <c>FILE_ANY_ACCESS</c> query, open to any user.</summary>
    Query,

    /// <summary>Read and write rights, without which Windows will not pass an ATA command. Administrator only.</summary>
    Command,
}

/// <summary>One control code, its input, the room for its answer, and the rights it needs.</summary>
internal sealed record DriveRequest(uint ControlCode, byte[] Input, int OutputBytes, DriveAccess Access);

/// <summary>
/// Everything the Health page asks a drive about its own condition (§7.6). Every request reads.
/// <c>DriveRequestsTests</c> decodes each one, so a command that could change a drive cannot be added
/// without that test saying so. Which disk holds a volume comes from <see cref="IStorageQueries"/>.
/// </summary>
internal static class DriveRequests
{
    /// <summary>The bytes of an ATA data sector, which every ATA command here returns.</summary>
    internal const int SectorBytes = 512;

    /// <summary><c>SENDCMDOUTPARAMS</c> up to its data: the size and the driver's status.</summary>
    internal const int AtaHeaderBytes = 16;

    /// <summary><c>SENDCMDINPARAMS</c> without the one-byte buffer it declares.</summary>
    internal const int AtaInputBytes = 32;

    /// <summary>Where <c>IDEREGS</c> begins in <c>SENDCMDINPARAMS</c>, after the buffer size.</summary>
    internal const int AtaRegistersOffset = 4;

    /// <summary><c>STORAGE_PROPERTY_QUERY</c> up to its parameters, then <c>STORAGE_PROTOCOL_SPECIFIC_DATA</c>.</summary>
    internal const int ProtocolQueryHeaderBytes = 8;

    internal const int ProtocolDataBytes = 40;

    internal const byte AtaSmartCommand = 0xB0;
    internal const byte AtaIdentifyCommand = 0xEC;
    internal const byte SmartReadThresholds = 0xD1;
    internal const byte SmartReadLog = 0xD5;
    internal const byte ErrorLogAddress = 0x01;
    internal const byte SelfTestLogAddress = 0x06;

    private const uint StorageGetDeviceNumber = 0x002D_1080;
    private const uint StoragePredictFailure = 0x002D_1100;
    private const uint DiskGetDriveGeometryEx = 0x0007_00A0;
    private const uint SmartRcvDriveData = 0x0007_C088;

    private const int AlignmentProperty = 6;
    private const int SeekPenaltyProperty = 7;
    private const int TrimProperty = 8;
    private const int DeviceProperty = 0;
    private const int DeviceProtocolSpecificProperty = 50;

    private const int ProtocolTypeNvme = 3;
    private const int NvmeDataTypeIdentify = 1;
    private const int NvmeDataTypeLogPage = 2;
    private const int NvmeIdentifyController = 1;
    private const int NvmeHealthLogPage = 2;
    private const int NvmeSelfTestLogPage = 6;

    private const int NvmeHealthBytes = 512;
    private const int NvmeIdentifyBytes = 4096;
    private const int NvmeSelfTestBytes = 564;

    /// <summary>The longest <c>STORAGE_DEVICE_DESCRIPTOR</c> a drive's four strings need.</summary>
    private const int DeviceDescriptorBytes = 1024;

    private const int SmallDescriptorBytes = 64;

    /// <summary><c>STORAGE_PREDICT_FAILURE</c>: a 32-bit flag and a sector.</summary>
    private const int PredictFailureBytes = sizeof(uint) + SectorBytes;

    private static readonly FrozenDictionary<DriveQuestion, DriveRequest> Table = new Dictionary<DriveQuestion, DriveRequest>
    {
        [DriveQuestion.DeviceNumber] = new(StorageGetDeviceNumber, [], SmallDescriptorBytes, DriveAccess.Query),
        [DriveQuestion.Device] = Property(DeviceProperty, DeviceDescriptorBytes),
        [DriveQuestion.SeekPenalty] = Property(SeekPenaltyProperty, SmallDescriptorBytes),
        [DriveQuestion.Trim] = Property(TrimProperty, SmallDescriptorBytes),
        [DriveQuestion.Alignment] = Property(AlignmentProperty, SmallDescriptorBytes),
        [DriveQuestion.Geometry] = new(DiskGetDriveGeometryEx, [], SmallDescriptorBytes * 4, DriveAccess.Query),
        [DriveQuestion.NvmeHealth] = Nvme(NvmeDataTypeLogPage, NvmeHealthLogPage, NvmeHealthBytes),
        [DriveQuestion.NvmeController] = Nvme(NvmeDataTypeIdentify, NvmeIdentifyController, NvmeIdentifyBytes),
        [DriveQuestion.NvmeSelfTest] = Nvme(NvmeDataTypeLogPage, NvmeSelfTestLogPage, NvmeSelfTestBytes),
        [DriveQuestion.PredictFailure] = new(StoragePredictFailure, [], PredictFailureBytes, DriveAccess.Query),
        [DriveQuestion.AtaThresholds] = Smart(SmartReadThresholds, logAddress: 1),
        [DriveQuestion.AtaIdentify] = Ata(features: 0, sectorNumber: 1, cylinderLow: 0, cylinderHigh: 0, AtaIdentifyCommand),
        [DriveQuestion.AtaErrorLog] = Smart(SmartReadLog, ErrorLogAddress),
        [DriveQuestion.AtaSelfTestLog] = Smart(SmartReadLog, SelfTestLogAddress),
    }.ToFrozenDictionary();

    internal static DriveRequest For(DriveQuestion question) => Table[question];

    private static DriveRequest Property(int property, int outputBytes) =>
        new(DeviceCalls.StorageQueryProperty, DeviceCalls.PropertyQuery(property), outputBytes, DriveAccess.Query);

    private static DriveRequest Nvme(int dataType, int requestValue, int dataBytes)
    {
        var total = ProtocolQueryHeaderBytes + ProtocolDataBytes + dataBytes;
        var input = new byte[total];
        var data = input.AsSpan(ProtocolQueryHeaderBytes);

        BinaryPrimitives.WriteInt32LittleEndian(input, DeviceProtocolSpecificProperty);
        BinaryPrimitives.WriteInt32LittleEndian(data, ProtocolTypeNvme);
        BinaryPrimitives.WriteInt32LittleEndian(data[4..], dataType);
        BinaryPrimitives.WriteInt32LittleEndian(data[8..], requestValue);
        BinaryPrimitives.WriteInt32LittleEndian(data[16..], ProtocolDataBytes);
        BinaryPrimitives.WriteInt32LittleEndian(data[20..], dataBytes);

        return new DriveRequest(DeviceCalls.StorageQueryProperty, input, total, DriveAccess.Query);
    }

    /// <summary>A SMART feature, which the ATA standard reaches through cylinder registers holding <c>C2 4F</c>.</summary>
    private static DriveRequest Smart(byte feature, byte logAddress) =>
        Ata(feature, logAddress, cylinderLow: 0x4F, cylinderHigh: 0xC2, AtaSmartCommand);

    private static DriveRequest Ata(byte features, byte sectorNumber, byte cylinderLow, byte cylinderHigh, byte command)
    {
        var input = new byte[AtaInputBytes];

        BinaryPrimitives.WriteInt32LittleEndian(input, SectorBytes);
        input[AtaRegistersOffset] = features;
        input[AtaRegistersOffset + 1] = 1;
        input[AtaRegistersOffset + 2] = sectorNumber;
        input[AtaRegistersOffset + 3] = cylinderLow;
        input[AtaRegistersOffset + 4] = cylinderHigh;
        input[AtaRegistersOffset + 5] = 0xA0;
        input[AtaRegistersOffset + 6] = command;

        return new DriveRequest(SmartRcvDriveData, input, AtaHeaderBytes + SectorBytes, DriveAccess.Command);
    }
}
