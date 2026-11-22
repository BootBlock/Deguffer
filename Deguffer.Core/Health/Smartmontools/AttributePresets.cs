using System.Buffers;
using System.Collections.Frozen;

namespace Deguffer.Core.Health.Smartmontools;

/// <summary>How smartctl's <c>-v</c> option says to read an attribute's raw bytes.</summary>
internal enum DatabaseRawFormat
{
    Raw8,
    Raw16,
    Raw48,
    Hex48,
    Raw56,
    Hex56,
    Raw64,
    Hex64,

    /// <summary>A 16-bit count, then two more 16-bit figures where they are not zero.</summary>
    Raw16WithRaw16,

    /// <summary>A 16-bit figure, then an average in the next 16 bits where it is not zero.</summary>
    Raw16WithAverage16,

    /// <summary>A 24-bit count, then three more bytes where they are not zero.</summary>
    Raw24WithRaw8,

    /// <summary>Two 24-bit counts.</summary>
    Raw24OverRaw24,

    /// <summary>A 24-bit count over a 32-bit count.</summary>
    Raw24OverRaw32,

    Seconds,
    Minutes,
    HalfMinutes,

    /// <summary>Hours in the low 32 bits, and milliseconds since the last whole hour in the 24 above.</summary>
    HoursAndMilliseconds,

    /// <summary>Degrees Celsius in the low byte, with figures above it that differ by maker.</summary>
    Celsius,

    /// <summary>Tenths of a degree Celsius in the low 16 bits.</summary>
    TenthsCelsius,
}

/// <summary>The kind of drive a <c>DEFAULT</c> entry's name applies to.</summary>
internal enum DriveMedium
{
    Any,
    HardDisk,
    SolidState,
}

/// <summary>What one <c>-v</c> option says about an attribute.</summary>
/// <param name="ByteOrder">The bytes that make the figure, most significant first, or empty for the format's own order.</param>
/// <param name="Name">The database's name for it, or null where the option gives none.</param>
internal sealed record AttributePreset(DatabaseRawFormat Format, string ByteOrder, string? Name, DriveMedium Only);

/// <summary>Which grammar an entry's options follow: the <c>DEFAULT</c> entry's, or a drive family's.</summary>
internal enum EntryKind
{
    /// <summary>May name the kind of drive a name is for, and may not reorder bytes.</summary>
    Default,

    /// <summary>May reorder bytes, and may not name a kind of drive.</summary>
    Family,
}

/// <summary>An entry's attribute options: by ID, and the one <c>-v N,...</c> gives every other ID.</summary>
internal sealed record EntryPresets(FrozenDictionary<byte, AttributePreset> ById, AttributePreset? Others)
{
    internal AttributePreset? Of(byte id) => ById.GetValueOrDefault(id) ?? Others;
}

/// <summary>
/// Reads an entry's option string, as smartctl's manual describes <c>-v</c>. Anything not read is a
/// fault in the whole entry, as smartctl treats it, so an entry is used whole or not at all: one
/// option skipped would leave its attribute with another option's meaning.
/// </summary>
internal static class AttributePresets
{
    private const int LongestName = 32;
    private const int LongestByteOrder = 8;
    private const int LongestArgument = 80;

    private static readonly char[] Space = [' ', '\t'];

    /// <summary>A raw byte 0 to 5, the reserved byte, the value, the worst value, or a zero.</summary>
    private static readonly SearchValues<char> ByteOrderCharacters = SearchValues.Create("012345rvwz");

    private static readonly FrozenDictionary<string, DatabaseRawFormat> Formats = new Dictionary<string, DatabaseRawFormat>(StringComparer.Ordinal)
    {
        ["raw8"] = DatabaseRawFormat.Raw8,
        ["raw16"] = DatabaseRawFormat.Raw16,
        ["raw48"] = DatabaseRawFormat.Raw48,
        ["hex48"] = DatabaseRawFormat.Hex48,
        ["raw56"] = DatabaseRawFormat.Raw56,
        ["hex56"] = DatabaseRawFormat.Hex56,
        ["raw64"] = DatabaseRawFormat.Raw64,
        ["hex64"] = DatabaseRawFormat.Hex64,
        ["raw16(raw16)"] = DatabaseRawFormat.Raw16WithRaw16,
        ["raw16(avg16)"] = DatabaseRawFormat.Raw16WithAverage16,
        ["raw24(raw8)"] = DatabaseRawFormat.Raw24WithRaw8,
        ["raw24/raw24"] = DatabaseRawFormat.Raw24OverRaw24,
        ["raw24/raw32"] = DatabaseRawFormat.Raw24OverRaw32,
        ["sec2hour"] = DatabaseRawFormat.Seconds,
        ["min2hour"] = DatabaseRawFormat.Minutes,
        ["halfmin2hour"] = DatabaseRawFormat.HalfMinutes,
        ["msec24hour32"] = DatabaseRawFormat.HoursAndMilliseconds,
        ["tempminmax"] = DatabaseRawFormat.Celsius,
        ["temp10x"] = DatabaseRawFormat.TenthsCelsius,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The older spellings smartctl's manual says it still accepts, each as its current form.</summary>
    private static readonly FrozenDictionary<string, string> OldForms = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["9,halfminutes"] = "9,halfmin2hour,Power_On_Half_Minutes",
        ["9,minutes"] = "9,min2hour,Power_On_Minutes",
        ["9,seconds"] = "9,sec2hour,Power_On_Seconds",
        ["9,temp"] = "9,tempminmax,Temperature_Celsius",
        ["192,emergencyretractcyclect"] = "192,raw48,Emerg_Retract_Cycle_Ct",
        ["193,loadunload"] = "193,raw24/raw24",
        ["194,10xCelsius"] = "194,temp10x,Temperature_Celsius_x10",
        ["194,unknown"] = "194,raw48,Unknown_Attribute",
        ["197,increasing"] = "197,raw48,Total_Pending_Sectors",
        ["198,offlinescanuncsectorct"] = "198,raw48,Offline_Scan_UNC_SectCt",
        ["198,increasing"] = "198,raw48,Total_Offl_Uncorrectabl",
        ["200,writeerrorcount"] = "200,raw48,Write_Error_Count",
        ["201,detectedtacount"] = "201,raw48,Detected_TA_Count",
        ["220,temp"] = "220,tempminmax,Temperature_Celsius",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The attribute options in <paramref name="presets"/>, or null where any option cannot be read.</summary>
    internal static EntryPresets? Read(string presets, EntryKind kind)
    {
        var byId = new Dictionary<byte, AttributePreset>();
        AttributePreset? others = null;
        var words = presets.Split(Space, StringSplitOptions.RemoveEmptyEntries);

        if (words.Length % 2 != 0)
        {
            return null;
        }

        for (var i = 0; i < words.Length; i += 2)
        {
            var (option, argument) = (words[i], words[i + 1]);

            if (argument.Length > LongestArgument)
            {
                return null;
            }

            switch (option)
            {
                case "-v" when Attribute(argument, kind) is ({ } id, { } preset):
                    if (id == 0)
                    {
                        // smartctl's rule: an ID's own option outranks N whatever the order, and the first N holds.
                        others ??= preset;
                    }
                    else
                    {
                        byId[id] = preset;
                    }

                    break;
                case "-F" or "-d":
                    // A firmware fault to work around, or a USB bridge's type: neither says what an attribute means.
                    break;
                default:
                    return null;
            }
        }

        return new EntryPresets(byId.ToFrozenDictionary(), others);
    }

    /// <summary>One <c>-v</c> argument: <c>ID,FORMAT[:BYTEORDER][,NAME[,HDD|SSD]]</c>, where ID 0 stands for N.</summary>
    private static (byte? Id, AttributePreset? Preset) Attribute(string argument, EntryKind kind)
    {
        var parts = OldForms.GetValueOrDefault(argument, argument).Split(',');

        if (parts.Length is < 2 or > 4 || (parts.Length == 4 && kind is not EntryKind.Default) || Id(parts[0]) is not { } id)
        {
            return (null, null);
        }

        var colon = parts[1].IndexOf(':');
        var (format, order) = colon >= 0 ? (parts[1][..colon], parts[1][(colon + 1)..]) : (parts[1], string.Empty);

        // smartctl marks two counters that never fall back with a plus; that changes nothing read here.
        format = format.TrimEnd('+');

        if (!Formats.TryGetValue(format, out var known) || (colon >= 0 && !IsByteOrder(order, kind)))
        {
            return (null, null);
        }

        var name = parts.Length > 2 ? parts[2] : null;

        if (name is { Length: 0 or > LongestName } || (id == 0 && parts.Length == 4))
        {
            return (null, null);
        }

        DriveMedium? only = parts.Length < 4 ? DriveMedium.Any : parts[3] switch
        {
            "HDD" => DriveMedium.HardDisk,
            "SSD" => DriveMedium.SolidState,
            _ => null,
        };

        return only is { } medium ? (id, new AttributePreset(known, order, name, medium)) : (null, null);
    }

    private static byte? Id(string text) =>
        text == "N" ? (byte)0
        : text.Length is > 0 and <= 3 && !text.AsSpan().ContainsAnyExceptInRange('0', '9') && int.Parse(text, null) is >= 1 and <= byte.MaxValue and var id ? (byte)id
        : null;

    private static bool IsByteOrder(string order, EntryKind kind) =>
        kind is EntryKind.Family && order.Length is > 0 and <= LongestByteOrder && !order.AsSpan().ContainsAnyExcept(ByteOrderCharacters);
}
