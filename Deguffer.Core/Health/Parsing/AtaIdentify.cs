using System.Buffers.Binary;

namespace Deguffer.Core.Health.Parsing;

/// <summary>The parts of ATA IDENTIFY DEVICE the page uses, which only a command handle can read.</summary>
/// <param name="RotationRpm">The spindle speed, 0 for a drive that does not spin, or null where it does not say.</param>
/// <param name="FastestGeneration">The highest SATA generation it supports, or 0 where it does not say.</param>
/// <param name="CurrentGeneration">The SATA generation the link runs at now, or 0 where it does not say.</param>
/// <param name="Model">The model number, without the spaces that pad it, which is what smartctl matches a drive database by.</param>
/// <param name="Firmware">The firmware revision, likewise.</param>
internal readonly record struct AtaIdentify(int? RotationRpm, int FastestGeneration, int CurrentGeneration, string Model, string Firmware)
{
    private const int FirmwareWord = 23;
    private const int FirmwareWords = 4;
    private const int ModelWord = 27;
    private const int ModelWords = 20;
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

        identify = new AtaIdentify(
            Rotation(Word(sector, RotationWord)),
            fastest,
            current is > 0 and <= HighestGeneration ? current : 0,
            Text(sector, ModelWord, ModelWords),
            Text(sector, FirmwareWord, FirmwareWords));
        return true;
    }

    private static int? Rotation(int word) => word switch
    {
        NonRotating => 0,
        >= FirstValidRpm and <= LastValidRpm => word,
        _ => null,
    };

    /// <summary>
    /// An ATA string: two characters to a word, the first in the word's high byte, padded with spaces.
    /// Anything but printable ASCII makes it no string at all, as a drive that leaves it unset does.
    /// </summary>
    private static string Text(ReadOnlySpan<byte> sector, int firstWord, int words)
    {
        Span<char> text = stackalloc char[words * 2];

        for (var i = 0; i < words; i++)
        {
            var word = Word(sector, firstWord + i);
            text[2 * i] = (char)(word >> 8);
            text[(2 * i) + 1] = (char)(word & 0xFF);
        }

        return text.ContainsAnyExceptInRange(' ', '~') ? string.Empty : text.Trim(' ').ToString();
    }

    private static int HighestSet(int bits) => bits == 0 ? 0 : 32 - int.LeadingZeroCount(bits);

    private static int Word(ReadOnlySpan<byte> sector, int word) =>
        BinaryPrimitives.ReadUInt16LittleEndian(sector[(word * sizeof(ushort))..]);
}
