using System.Globalization;
using Deguffer.Core.Duplicates;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Benchmark;

/// <summary>
/// The value one option on the command line takes, read from its text: each answers whether the
/// text is a value the option accepts, and the value where it is. Apart from
/// <see cref="BenchmarkRequest"/>, which decides which option each route takes.
/// </summary>
internal static class OptionValues
{
    public static bool TryCommand(string? text, out string? command)
    {
        command = string.IsNullOrWhiteSpace(text) ? null : text;
        return command is not null;
    }

    public static bool TryListReads(string? text, out AttributeListReads listReads)
    {
        listReads = text is "beside" ? AttributeListReads.BesideFirstPass : AttributeListReads.AfterFirstPass;
        return text is "after" or "beside";
    }

    public static bool TryYield(string? text, out bool yields)
    {
        yields = text is not "normal";
        return text is "yield" or "normal";
    }

    public static bool TryRacer(string? text, out Route racer)
    {
        racer = text is "walk" ? Route.Walk : Route.Sources;
        return text is "sources" or "walk";
    }

    public static bool TryNumber(string? text, int minimum, int maximum, out int number) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number)
        && number >= minimum
        && number <= maximum;

    public static bool TryNumber(string? text, long minimum, long maximum, out long number) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number)
        && number >= minimum
        && number <= maximum;

    /// <summary>
    /// A checksum this machine offers, by the name other tools print it under, in any case and with or
    /// without its hyphen: <c>sha256</c>, <c>SHA-256</c> and <c>sha-256</c> are one.
    /// </summary>
    public static bool TryChecksum(string? text, out ChecksumAlgorithm algorithm)
    {
        var named = text is null
            ? null
            : ChecksumAlgorithms.Offered
                .Where(offered => string.Equals(Unhyphenated(offered.Name()), Unhyphenated(text), StringComparison.OrdinalIgnoreCase))
                .Cast<ChecksumAlgorithm?>()
                .SingleOrDefault();

        algorithm = named ?? ChecksumAlgorithm.XxHash128;
        return named is not null;
    }

    /// <summary>The name a checksum is given on the command line, as other tools print it, in lower case.</summary>
    public static string Token(ChecksumAlgorithm algorithm) => algorithm.Name().ToLowerInvariant();

    private static string Unhyphenated(string name) => name.Replace("-", string.Empty, StringComparison.Ordinal);

    public static bool TryCache(string? text, out CacheStart cache)
    {
        cache = text is "warm" ? CacheStart.Warm : CacheStart.Cold;
        return text is "cold" or "warm";
    }

    public static bool TryReadBy(string? text, out LinkRead readBy)
    {
        readBy = text is "handle" ? LinkRead.Handle : LinkRead.Name;
        return text is "name" or "handle";
    }

    public static bool TryOnOff(string? text, out bool on)
    {
        on = text is "on";
        return text is "on" or "off";
    }
}
