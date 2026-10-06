using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Deguffer.Core.Providers;

namespace Deguffer.Core.VirtualDisks;

/// <summary>One line of <c>docker system df</c>: a kind of thing Docker keeps, and what it takes.</summary>
/// <param name="Type">As Docker names it: <c>Images</c>, <c>Containers</c>, <c>Local Volumes</c> or <c>Build Cache</c>.</param>
/// <param name="Size">What all of that kind takes inside the disk, in bytes.</param>
/// <param name="Reclaimable">What Docker says is not in use, in bytes.</param>
public sealed record DockerUsageRow(string Type, long Size, long Reclaimable);

/// <summary>
/// Docker's own account of the space inside its data disk, as <c>docker system df</c> gives it.
///
/// <para>§5.4: this is the figure the file system cannot give. The host sees one large file, and only
/// the engine knows how much of what is inside it is images nothing uses, stopped containers, unused
/// volumes and build cache.</para>
/// </summary>
public sealed partial record DockerDiskUsage(IReadOnlyList<DockerUsageRow> Rows)
{
    /// <summary>The <c>Type</c> Docker gives unused volumes, which hold data rather than copies.</summary>
    public const string VolumesType = "Local Volumes";

    public long Size => Rows.Sum(row => row.Size);

    public long Reclaimable => Rows.Sum(row => row.Reclaimable);

    /// <summary>
    /// Reads the output of <c>docker system df --format "{{json .}}"</c>: one JSON object a line, each
    /// with the five string fields Docker's formatter writes. Null where no line could be read, so a
    /// changed format reads as "Docker did not say" rather than as an empty disk.
    /// </summary>
    public static DockerDiskUsage? Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var rows = new List<DockerUsageRow>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Row(line) is not { } row)
            {
                return null;
            }

            rows.Add(row);
        }

        return rows.Count == 0 ? null : new DockerDiskUsage(rows);
    }

    private static DockerUsageRow? Row(string line)
    {
        using var document = BoundedJsonFile.Parse(Encoding.UTF8.GetBytes(line));

        if (document?.RootElement is not { } record
            || BoundedJsonFile.StringProperty(record, "Type") is not { Length: > 0 } type
            || Bytes(BoundedJsonFile.StringProperty(record, "Size")) is not { } size
            || Bytes(BoundedJsonFile.StringProperty(record, "Reclaimable")) is not { } reclaimable)
        {
            return null;
        }

        return new DockerUsageRow(type, size, reclaimable);
    }

    /// <summary>
    /// A size as Docker's <c>HumanSize</c> writes it: a decimal number, an optional space, and a unit
    /// in powers of 1000, such as <c>16.43MB</c> or <c>212B</c>. A reclaimable figure carries a share in
    /// brackets after it, <c>11.63MB (70%)</c>, which is not part of the size.
    /// </summary>
    internal static long? Bytes(string? text)
    {
        if (text is null || HumanSize().Match(text) is not { Success: true } match
            || !double.TryParse(match.Groups["number"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        var power = match.Groups["unit"].Value.ToUpperInvariant() switch
        {
            "B" => 0,
            "KB" => 1,
            "MB" => 2,
            "GB" => 3,
            "TB" => 4,
            "PB" => 5,
            _ => -1,
        };

        return power < 0 ? null : (long)Math.Round(number * Math.Pow(1000, power));
    }

    [GeneratedRegex(@"^\s*(?<number>\d+(\.\d+)?)\s?(?<unit>[a-zA-Z]+)\s*(\(\d+%\))?\s*$")]
    private static partial Regex HumanSize();
}
