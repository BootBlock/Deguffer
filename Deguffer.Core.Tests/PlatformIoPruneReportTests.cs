using Deguffer.Core.Providers;

namespace Deguffer.Core.Tests;

/// <summary>
/// <c>pio system prune</c> has no machine-readable output, so its printed report is the only source
/// for what it would remove. These cover the parse of it, because everything downstream — whether a
/// multi-gigabyte row appears at all, and what it claims — rests on this one reading.
///
/// The samples are the shapes PlatformIO 6.1.19 actually prints: the observed zero report from the
/// surveyed machine, and the table <c>tabulate</c> produces where something was flagged.
/// </summary>
public sealed class PlatformIoPruneReportTests
{
    /// <summary>
    /// What the surveyed machine printed, verbatim apart from the line endings: two installed
    /// <c>espressif32</c> versions between them referenced every tool package, so nothing was
    /// reclaimable.
    /// </summary>
    private const string NothingToPrune =
        """
        Dry run mode (do not prune, only show data that will be removed)

        Prune unnecessary core packages:
        Calculating...
        Space on disk: 0B

        Prune unnecessary development platform packages:
        Calculating...
        Space on disk: 0B

        Total reclaimed space: 0B
        """;

    /// <summary>
    /// The same report from a machine that upgraded a platform in place, where the superseded
    /// toolchain is left behind. Column widths differ between the two tables on purpose: they are
    /// set by the longest package name, so nothing in the parse may depend on them.
    /// </summary>
    private const string SupersededToolchains =
        """
        Dry run mode (do not prune, only show data that will be removed)

        Prune unnecessary core packages:
        Calculating...
        Package                            Version    Size
        ---------------------------------  ---------  ------
        platformio/tool-scons @ ~4.40400.0  4.40400.0  1.50MB
        Space on disk: 1.50MB

        Prune unnecessary development platform packages:
        Calculating...
        Package                                          Version              Size
        -----------------------------------------------  -------------------  ---------
        platformio/toolchain-xtensa-esp32 @ ~8.4.0       8.4.0+2021r2-patch5  256.34MB
        platformio/framework-arduinoespressif32 @ ~3.20  3.20017.0            98.10MB
        Space on disk: 354.44MB

        Total reclaimed space: 355.94MB
        """;

    [Fact]
    public void ReadsZeroWhenPlatformIoFlaggedNothing()
    {
        var preview = PlatformIoPruneReport.TryRead(NothingToPrune);

        Assert.NotNull(preview);
        Assert.Equal(0, preview.Bytes);
        Assert.Empty(preview.Packages);
    }

    /// <summary>
    /// A zero is a real answer and must stay distinguishable from an unreadable report. One means
    /// there is nothing to offer, the other that nothing may be offered — see
    /// <see cref="AnswersNullWhenTheReportNeverReachedItsTotal"/>.
    /// </summary>
    [Fact]
    public void ReadsTheTotalAcrossBothCategories()
    {
        var preview = PlatformIoPruneReport.TryRead(SupersededToolchains);

        // 355.94 × 1,048,576 = 373,230,141.44, which is what "355.94MB" can mean to two places.
        Assert.NotNull(preview);
        Assert.Equal(373_230_141, preview.Bytes);
    }

    /// <summary>
    /// The evidence behind the row. Every flagged package is read, from both categories, and a name
    /// containing spaces survives — PlatformIO humanises a spec as <c>owner/name @ requirement</c>.
    /// </summary>
    [Fact]
    public void NamesEveryFlaggedPackageAcrossBothTables()
    {
        var preview = PlatformIoPruneReport.TryRead(SupersededToolchains);

        Assert.NotNull(preview);
        Assert.Equal(3, preview.Packages.Count);

        Assert.Equal("platformio/tool-scons @ ~4.40400.0", preview.Packages[0].Name);
        Assert.Equal("1.50MB", preview.Packages[0].Size);

        Assert.Equal("platformio/toolchain-xtensa-esp32 @ ~8.4.0", preview.Packages[1].Name);
        Assert.Equal("256.34MB", preview.Packages[1].Size);

        Assert.Equal("platformio/framework-arduinoespressif32 @ ~3.20", preview.Packages[2].Name);
    }

    /// <summary>
    /// The header line, the <c>Calculating...</c> line and each category's own summary are not rows,
    /// and none of them may arrive as a package.
    ///
    /// The summary is the trap. <c>Space on disk: 1.50MB</c> is exactly row-shaped — three or more
    /// fields ending in a size — and PlatformIO prints it directly beneath the last row with no
    /// blank line between, so nothing about the layout ends the table for us.
    /// </summary>
    [Fact]
    public void TakesNoFurnitureFromTheReportAsAPackage()
    {
        var preview = PlatformIoPruneReport.TryRead(SupersededToolchains);

        Assert.NotNull(preview);
        Assert.All(preview.Packages, package =>
        {
            Assert.StartsWith("platformio/", package.Name, StringComparison.Ordinal);
            Assert.DoesNotContain("Space", package.Name, StringComparison.Ordinal);
            Assert.DoesNotContain("Package", package.Name, StringComparison.Ordinal);
            Assert.DoesNotContain("Calculating", package.Name, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// The command prints its total last and only on the path that completed, so a report without
    /// one is a run that failed part way. The caller must offer nothing rather than read the
    /// half-report as a zero.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("Usage: pio system prune [OPTIONS]")] // an older build that rejects the flags
    [InlineData("Prune unnecessary core packages:\nCalculating...\nSpace on disk: 4.20GB")]
    [InlineData("Total reclaimed space: lots")]
    [InlineData("Total reclaimed space:")]
    public void AnswersNullWhenTheReportNeverReachedItsTotal(string output)
    {
        Assert.Null(PlatformIoPruneReport.TryRead(output));
    }

    /// <summary>
    /// Every form <c>fs.humanize_file_size</c> emits: a bare count below 1 KB, a whole number of
    /// units where the division came out even, and two decimal places otherwise.
    /// </summary>
    [Theory]
    [InlineData("0B", 0L)]
    [InlineData("926B", 926L)]
    [InlineData("1KB", 1024L)]
    [InlineData("926.11KB", 948_337L)]
    [InlineData("2MB", 2_097_152L)]
    [InlineData("1.50GB", 1_610_612_736L)]
    [InlineData("1TB", 1_099_511_627_776L)]
    public void ReadsEveryFormPlatformIoPrintsASizeIn(string size, long expected)
    {
        var preview = PlatformIoPruneReport.TryRead($"Total reclaimed space: {size}");

        Assert.NotNull(preview);
        Assert.Equal(expected, preview.Bytes);
    }

    /// <summary>
    /// A figure that cannot be read is not a figure. Deguffer never invents one from the packages
    /// directory instead, so the whole of the safety here is that a bad parse answers null rather
    /// than a number.
    /// </summary>
    [Theory]
    [InlineData("B")]
    [InlineData("12")]
    [InlineData("1.5XB")]
    [InlineData("-1KB")]
    [InlineData("1,5KB")] // a comma decimal separator is not what Python writes
    [InlineData("99999999999999999999999YB")] // more bytes than a long holds
    public void AnswersNullForASizeItCannotRead(string size)
    {
        Assert.Null(PlatformIoPruneReport.TryRead($"Total reclaimed space: {size}"));
    }

    /// <summary>The pipe preserves carriage returns, and PlatformIO's output carries them.</summary>
    [Fact]
    public void ReadsAReportThatArrivedWithWindowsLineEndings()
    {
        var preview = PlatformIoPruneReport.TryRead(
            SupersededToolchains.ReplaceLineEndings("\r\n"));

        Assert.NotNull(preview);
        Assert.Equal(373_230_141, preview.Bytes);
        Assert.Equal(3, preview.Packages.Count);
    }
}
