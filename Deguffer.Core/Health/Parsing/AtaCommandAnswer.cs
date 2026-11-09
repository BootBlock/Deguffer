namespace Deguffer.Core.Health.Parsing;

/// <summary>Takes the sector out of a <c>SMART_RCV_DRIVE_DATA</c> answer.</summary>
internal static class AtaCommandAnswer
{
    /// <summary><c>DRIVERSTATUS.bDriverError</c>, after the 32-bit buffer size.</summary>
    private const int DriverErrorOffset = 4;

    /// <summary>
    /// False where the answer is short, or where the driver says the command failed. The call itself
    /// can succeed while the status reports a failure, and the sector then reads as all zeroes. Only
    /// the driver error is read, because Windows defines <c>bIDEError</c> as valid only alongside one.
    /// </summary>
    internal static bool TrySector(ReadOnlySpan<byte> answer, out ReadOnlySpan<byte> sector)
    {
        sector = default;

        if (answer.Length < DriveRequests.AtaHeaderBytes + DriveRequests.SectorBytes || answer[DriverErrorOffset] != 0)
        {
            return false;
        }

        sector = answer.Slice(DriveRequests.AtaHeaderBytes, DriveRequests.SectorBytes);
        return true;
    }
}
