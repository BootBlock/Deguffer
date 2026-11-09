using System.Buffers.Binary;

namespace Deguffer.Core.Health.Parsing;

/// <summary>The parts of ATA IDENTIFY DEVICE the page uses, which only a command handle can read.</summary>
/// <param name="RotationRpm">The spindle speed, 0 for a drive that does not spin, or null where it does not say.</param>
/// <param name="FastestGeneration">The highest SATA generation it supports, or 0 where it does not say.</param>
/// <param name="CurrentGeneration">The SATA generation the link runs at now, or 0 where it does not say.</param>
internal readonly record struct AtaIdentify(int? RotationRpm, int FastestGeneration, int CurrentGeneration)
{
    private const int CapabilitiesWord = 76;
    private const int CurrentSpeedWord = 77;
    private const int RotationWord = 217;
    private const int NonRotating = 1;
    private const int FirstValidRpm = 0x0401;
    private const int LastValidRpm = 0xFFFE;
    private const int GenerationShift = 1;
    private const int GenerationMask = 0b111;
    private const int HighestGeneration = 3;

    internal static bool TryRead(ReadOnlySpan<byte> sector, out AtaIdentify identify)
    {
        identify = default;

        if (sector.Length < DriveRequests.SectorBytes)
        {
            return false;
        }

        var capabilities = Word(sector, CapabilitiesWord);

        // All zeroes or all ones marks a word the drive leaves unused.
        var fastest = capabilities is 0 or 0xFFFF ? 0 : HighestSet((capabilities >> GenerationShift) & GenerationMask);
        var current = (Word(sector, CurrentSpeedWord) >> GenerationShift) & GenerationMask;

        identify = new AtaIdentify(Rotation(Word(sector, RotationWord)), fastest, current is > 0 and <= HighestGeneration ? current : 0);
        return true;
    }

    private static int? Rotation(int word) => word switch
    {
        NonRotating => 0,
        >= FirstValidRpm and <= LastValidRpm => word,
        _ => null,
    };

    private static int HighestSet(int bits) => bits == 0 ? 0 : 32 - int.LeadingZeroCount(bits);

    private static int Word(ReadOnlySpan<byte> sector, int word) =>
        BinaryPrimitives.ReadUInt16LittleEndian(sector[(word * sizeof(ushort))..]);
}
