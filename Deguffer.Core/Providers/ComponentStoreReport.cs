using System.Globalization;

namespace Deguffer.Core.Providers;

/// <summary>
/// What <c>DISM /Online /Cleanup-Image /AnalyzeComponentStore</c> says about the component store,
/// <c>C:\Windows\WinSxS</c>, read from its English report.
///
/// <para><b>The report is text, and there is no other route to it.</b> Neither the DISM API nor a
/// PowerShell cmdlet exposes the analysis, so the command is run with <c>/English</c> and read by its
/// labels, which the switch fixes whatever language Windows is displayed in.</para>
///
/// <para><b>Sizes are decimal.</b> A store whose files summed to 23,367,207,239 bytes was reported as
/// "23.36 GB", so a gigabyte here is 10^9 bytes, not 2^30, and each figure is exact only to the two
/// decimal places the report gives.</para>
/// </summary>
/// <param name="ExplorerSize">The size Explorer shows, which counts every hard-linked file as if the store owned it.</param>
/// <param name="ActualSize">The store's real size, with what it shares with Windows counted once.</param>
/// <param name="SharedWithWindows">Files hard-linked into Windows itself, which no cleanup frees.</param>
/// <param name="BackupsAndDisabledFeatures">Superseded components, and the payload of features that are switched off.</param>
/// <param name="CacheAndTemporaryData">What the servicing stack keeps to run faster.</param>
/// <param name="LastCleanup">When Windows last cleaned the store, as the report states it, or null where it does not.</param>
/// <param name="ReclaimablePackages">How many superseded packages a cleanup can remove.</param>
/// <param name="CleanupRecommended">Windows' own verdict on whether the store is worth cleaning.</param>
public sealed record ComponentStoreReport(
    long ExplorerSize,
    long ActualSize,
    long SharedWithWindows,
    long BackupsAndDisabledFeatures,
    long CacheAndTemporaryData,
    string? LastCleanup,
    int ReclaimablePackages,
    bool CleanupRecommended)
{
    private const string ExplorerLabel = "Windows Explorer Reported Size of Component Store";
    private const string ActualLabel = "Actual Size of Component Store";
    private const string SharedLabel = "Shared with Windows";
    private const string BackupsLabel = "Backups and Disabled Features";
    private const string CacheLabel = "Cache and Temporary Data";
    private const string LastCleanupLabel = "Date of Last Cleanup";
    private const string PackagesLabel = "Number of Reclaimable Packages";
    private const string RecommendedLabel = "Component Store Cleanup Recommended";

    /// <summary>
    /// The store's overhead as Microsoft does the arithmetic: what is neither shared with Windows nor
    /// needed by it. It is the most a cleanup could free, and not what one frees, because the payload of
    /// a switched-off feature is counted here and no cleanup removes it.
    /// </summary>
    public long Overhead => BackupsAndDisabledFeatures + CacheAndTemporaryData;

    /// <summary>
    /// The report in <paramref name="output"/>, or null where any figure a plan relies on is missing
    /// or unreadable. Half a report is not a report: a missing actual size would be read as an empty
    /// store.
    /// </summary>
    public static ComponentStoreReport? Parse(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in output.Split('\n'))
        {
            var separator = line.IndexOf(" : ", StringComparison.Ordinal);

            if (separator > 0)
            {
                values.TryAdd(line[..separator].Trim(), line[(separator + 3)..].Trim());
            }
        }

        if (Size(values, ExplorerLabel) is not { } explorer
            || Size(values, ActualLabel) is not { } actual
            || Size(values, SharedLabel) is not { } shared
            || Size(values, BackupsLabel) is not { } backups
            || Size(values, CacheLabel) is not { } cache
            || !values.TryGetValue(PackagesLabel, out var packagesText)
            || !int.TryParse(packagesText, NumberStyles.None, CultureInfo.InvariantCulture, out var packages)
            || !values.TryGetValue(RecommendedLabel, out var recommended)
            || recommended is not ("Yes" or "No"))
        {
            return null;
        }

        return new ComponentStoreReport(
            explorer,
            actual,
            shared,
            backups,
            cache,
            values.TryGetValue(LastCleanupLabel, out var last) && last.Length > 0 ? last : null,
            packages,
            recommended == "Yes");
    }

    private static long? Size(Dictionary<string, string> values, string label) =>
        values.TryGetValue(label, out var text) ? ParseSize(text) : null;

    /// <summary>
    /// A figure such as "12.98 GB" or "0 bytes", in bytes. <c>/English</c> fixes the words and not the
    /// digits, so a decimal comma is read as well as a decimal point.
    /// </summary>
    internal static long? ParseSize(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 2)
        {
            return null;
        }

        decimal? multiplier = parts[1] switch
        {
            "bytes" or "byte" => 1m,
            "KB" => 1e3m,
            "MB" => 1e6m,
            "GB" => 1e9m,
            "TB" => 1e12m,
            _ => null,
        };

        return multiplier is { } scale && Number(parts[0]) is { } number
            ? (long)Math.Round(number * scale, MidpointRounding.AwayFromZero)
            : null;
    }

    /// <summary>
    /// The number, whichever separators the display language gave it. A separator followed by one or
    /// two digits at the end is the decimal one, since the report gives two decimal places, and any
    /// other separator groups thousands.
    /// </summary>
    private static decimal? Number(string text)
    {
        var last = text.LastIndexOfAny(['.', ',']);
        var fraction = last >= 0 && text.Length - last - 1 is 1 or 2 ? text[(last + 1)..] : string.Empty;
        var whole = (fraction.Length > 0 ? text[..last] : text).Replace(".", string.Empty, StringComparison.Ordinal)
            .Replace(",", string.Empty, StringComparison.Ordinal);

        return decimal.TryParse(
                fraction.Length > 0 ? $"{whole}.{fraction}" : whole,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var number)
            ? number
            : null;
    }
}
