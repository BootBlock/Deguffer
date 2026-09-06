using System.Globalization;

namespace Deguffer.Core.Providers;

/// <summary>
/// One package PlatformIO's own prune named as unnecessary.
///
/// Kept per package rather than collapsed into the total because the row is a claim about
/// installed software — "these toolchains are unreferenced" — and a claim about installed software
/// should name it. The user can check any one of these against their own projects before agreeing.
/// </summary>
/// <param name="Name">
/// PlatformIO's own humanised spec, which is <c>owner/name</c> and, where the package was installed
/// against a requirement, that requirement after an <c>@</c>. It therefore contains spaces.
/// </param>
/// <param name="Size">
/// PlatformIO's own printed size, verbatim. This is evidence rather than arithmetic: it is shown to
/// the user beside the package and never summed, so re-rendering it in Deguffer's own units would
/// only introduce a second figure to disagree with the tool's.
/// </param>
internal sealed record PlatformIoPrunablePackage(string Name, string Size);

/// <summary>
/// What <c>pio system prune --core-packages --platform-packages --dry-run</c> reports it would
/// remove.
/// </summary>
/// <param name="Bytes">
/// PlatformIO's own total. It is printed in a humanised form rounded to two decimal places, so it
/// is an approximation by construction and is carried as one, never as a measurement.
/// </param>
/// <param name="Packages">
/// The per-package table, largest first, which PlatformIO prints only where it flagged something.
/// Empty is a legitimate answer for a zero total, and also for a report whose table this could not
/// read — the total is the figure, and the table is evidence to show alongside it.
/// </param>
internal sealed record PlatformIoPrunePreview(
    long Bytes,
    IReadOnlyList<PlatformIoPrunablePackage> Packages);

/// <summary>
/// Reads what <c>pio system prune --dry-run</c> printed.
///
/// <para><b>Scraping text, and not by preference.</b> The other subprocess this provider runs is
/// asked for <c>--json-output</c>, on the stated ground that field names are a documented contract
/// while the alignment of a printed table is not. <c>pio system prune</c> offers no machine-readable
/// form at all, so the choice is between reading the printed report and never offering the row —
/// and the row is where the gigabytes in this directory actually are. The parse is therefore
/// written to lean on as little of the layout as it can: the figure comes from the
/// <c>Total reclaimed space:</c> line, and a table row is read from its end, last field the size and
/// the one before it the version, so the column widths — which move with the longest package name —
/// carry no meaning.</para>
///
/// <para><b>An unreadable report answers null, and the caller then offers nothing.</b> The only
/// substitute figure available is a direct measure of <c>packages</c>, which counts every toolchain
/// an installed platform still requires: on the surveyed machine that was all 5,670.9 MB of it,
/// against a true reclaim of zero. Guessing in that direction is what §5.2 exists to refuse.</para>
/// </summary>
internal static class PlatformIoPruneReport
{
    private const string TotalPrefix = "Total reclaimed space:";

    /// <summary>What each category prints beneath its own table, which is where that table ends.</summary>
    private const string CategoryTotalPrefix = "Space on disk:";

    /// <summary>Ascending powers of 1024, in the order <c>fs.humanize_file_size</c> emits them.</summary>
    private const string Units = "KMGTPEZY";

    /// <summary>
    /// The preview, or null where the report does not carry one.
    ///
    /// <para>The total line is the authority, and the exit code is not consulted. That line is the
    /// last thing the command prints and only the completed path reaches it: a run that failed while
    /// listing candidates raises before either category is summed, so a report carrying a total is a
    /// report that finished. An exit code would say the same thing less directly.</para>
    /// </summary>
    public static PlatformIoPrunePreview? TryRead(string standardOutput)
    {
        long? total = null;
        var packages = new List<PlatformIoPrunablePackage>();
        var inTable = false;

        foreach (var raw in standardOutput.Split('\n'))
        {
            // Trimming also disposes of the carriage return, which the pipe preserves.
            var line = raw.Trim();

            if (line.StartsWith(TotalPrefix, StringComparison.Ordinal))
            {
                total = TryReadSize(line[TotalPrefix.Length..].Trim());
                continue;
            }

            // The category's own summary closes its table, and it has to be recognised rather than
            // left to fail the row test: "Space on disk: 1.50MB" is row-shaped, and read as one it
            // becomes a package called "Space on" whose size is the category total. PlatformIO
            // prints it directly beneath the last row, with no blank line to end the table instead.
            if (line.StartsWith(CategoryTotalPrefix, StringComparison.Ordinal))
            {
                inTable = false;
                continue;
            }

            if (IsRule(line))
            {
                inTable = true;
                continue;
            }

            if (!inTable)
            {
                continue;
            }

            // Anything else that is not a row ends the table too — a blank line, or output no
            // version of this command prints today. Reading on to the end instead would let the
            // second category's rows arrive without their own rule line ever having been seen.
            if (TryReadRow(line) is { } package)
            {
                packages.Add(package);
            }
            else
            {
                inTable = false;
            }
        }

        return total is { } bytes ? new PlatformIoPrunePreview(bytes, packages) : null;
    }

    /// <summary>
    /// The dashed line <c>tabulate</c> puts under its headers, which is what marks where the rows
    /// begin. Matched by shape rather than by width, because the width is the longest package name.
    /// </summary>
    private static bool IsRule(string line) =>
        line.Length >= 3 && line.Contains('-', StringComparison.Ordinal) && line.All(c => c is '-' or ' ');

    /// <summary>
    /// One row, read from the right. The size and the installed version are single fields and the
    /// package name is whatever precedes them — which is not one field: PlatformIO humanises a spec
    /// as <c>platformio/toolchain-xtensa-esp32 @ ~8.4.0</c>, with spaces in it.
    ///
    /// <para>The version column is skipped rather than kept. It is the resolved version and the name
    /// already carries the requirement the package was installed against, so showing both puts two
    /// near-identical version strings on one line and tells the user nothing the name did not.</para>
    ///
    /// <para>Parsing the size is the test of whether this is a row at all, and its <em>text</em> is
    /// what is kept — see <see cref="PlatformIoPrunablePackage.Size"/>. A header, a blank line or a
    /// category summary fails it, which is how the table's end is found.</para>
    /// </summary>
    private static PlatformIoPrunablePackage? TryReadRow(string line)
    {
        var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        return fields.Length >= 3 && TryReadSize(fields[^1]) is not null
            ? new PlatformIoPrunablePackage(string.Join(' ', fields[..^2]), fields[^1])
            : null;
    }

    /// <summary>
    /// One size as <c>fs.humanize_file_size</c> writes it: a bare byte count below 1 KB
    /// (<c>926B</c>), and otherwise a number against a power-of-1024 suffix, to two decimal places
    /// unless the division came out whole (<c>926.11KB</c>, <c>1KB</c>).
    ///
    /// <para>Invariant culture throughout, because the producer is a Python format string and always
    /// writes a full stop. Reading it under the user's culture would turn <c>926.11KB</c> into 92,611
    /// kilobytes on a machine set to a comma decimal separator.</para>
    /// </summary>
    private static long? TryReadSize(string text)
    {
        if (text.Length < 2 || text[^1] != 'B')
        {
            return null;
        }

        var body = text[..^1];
        var power = Units.IndexOf(body[^1]) + 1;

        if (power > 0)
        {
            body = body[..^1];
        }

        if (!decimal.TryParse(
                body, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            || value < 0
            || value > long.MaxValue)
        {
            return null;
        }

        // Scaled a step at a time with the ceiling checked each time, rather than multiplied out and
        // checked at the end. A yottabyte does not fit in a long and no size this tool will ever see
        // comes close, but malformed output is not bounded by what is plausible, and a decimal
        // large enough to overflow the multiplication itself would throw rather than answer null.
        for (var i = 0; i < power; i++)
        {
            value *= 1024;

            if (value > long.MaxValue)
            {
                return null;
            }
        }

        return (long)decimal.Round(value);
    }
}
