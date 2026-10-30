using Deguffer.Core.Providers;

namespace Deguffer.Core.Tests;

/// <summary>
/// The dry run's report, in the shape <c>plugins/commands/box/command/prune.rb</c> prints it through
/// <c>Vagrant::UI::Basic</c> (hashicorp/vagrant at <c>dc55920a</c>): the kept list padded to the
/// longest box name, a blank line, then one line per removal or the line saying there is none.
/// </summary>
public sealed class VagrantPruneReportTests
{
    private const string TwoBoxesOneSuperseded =
        """
        The following boxes will be kept...
        hashicorp/bionic64 (virtualbox, 1.0.282)
        generic/alpine38   (hyperv, 4.3.12)

        Checking for older boxes...
        Would remove hashicorp/bionic64 virtualbox 1.0.0
        Would remove hashicorp/bionic64 virtualbox 1.0.1
        """;

    [Fact]
    public void ReadsWhatPruneKeepsAndWhatItWouldRemove()
    {
        var preview = VagrantPruneReport.TryRead(TwoBoxesOneSuperseded);

        Assert.NotNull(preview);
        Assert.Equal(
            [new("hashicorp/bionic64", "virtualbox", "1.0.282"), new("generic/alpine38", "hyperv", "4.3.12")],
            preview.Kept);
        Assert.Equal(
            [new("hashicorp/bionic64", "virtualbox", "1.0.0"), new("hashicorp/bionic64", "virtualbox", "1.0.1")],
            preview.Removed);
    }

    /// <summary>Ruby's line feed comes through the pipe as a carriage return and a line feed.</summary>
    [Fact]
    public void ReadsLinesEndedTheWayWindowsWritesThem()
    {
        var preview = VagrantPruneReport.TryRead(TwoBoxesOneSuperseded.ReplaceLineEndings("\r\n"));

        Assert.NotNull(preview);
        Assert.Equal(2, preview.Removed.Count);
        Assert.Equal("1.0.282", preview.Kept[0].Version);
    }

    [Fact]
    public void ReadsAReportWithNothingToRemoveAsNothing()
    {
        var preview = VagrantPruneReport.TryRead(
            """
            The following boxes will be kept...
            hashicorp/bionic64 (virtualbox, 1.0.282)

            Checking for older boxes...
            No old versions of boxes to remove...
            """);

        Assert.NotNull(preview);
        Assert.Single(preview.Kept);
        Assert.Empty(preview.Removed);
    }

    /// <summary>Printed by <c>@env.ui.warn</c>, which Basic writes to standard output.</summary>
    [Fact]
    public void ReadsNoInstalledBoxesAsNothing()
    {
        var preview = VagrantPruneReport.TryRead("There are no installed boxes! Use `vagrant box add` to add some.\n");

        Assert.NotNull(preview);
        Assert.Empty(preview.Kept);
        Assert.Empty(preview.Removed);
    }

    /// <summary>
    /// A name holding spaces and a parenthesis of its own, and a name with a colon, which Vagrant
    /// prints as the logical name rather than as the folder's.
    /// </summary>
    [Fact]
    public void ReadsNamesWithSpacesParenthesesAndColons()
    {
        var preview = VagrantPruneReport.TryRead(
            """
            The following boxes will be kept...
            my box (lab)      (virtualbox, 2.0)
            registry.test:443 (libvirt, 1.2.3-beta.1)

            Checking for older boxes...
            Would remove my box (lab) virtualbox 1.0
            Would remove registry.test:443 libvirt 1.2.3-alpha
            """);

        Assert.NotNull(preview);
        Assert.Equal([new("my box (lab)", "virtualbox", "2.0"), new("registry.test:443", "libvirt", "1.2.3-beta.1")], preview.Kept);
        Assert.Equal([new("my box (lab)", "virtualbox", "1.0"), new("registry.test:443", "libvirt", "1.2.3-alpha")], preview.Removed);
    }

    /// <summary>
    /// Prune prints one line for each architecture of a version, and the provider says what that
    /// means, so the repeat is kept rather than folded.
    /// </summary>
    [Fact]
    public void KeepsAVersionNamedOncePerArchitecture()
    {
        var preview = VagrantPruneReport.TryRead(
            """
            The following boxes will be kept...
            hashicorp/bionic64 (virtualbox, 2.0)

            Checking for older boxes...
            Would remove hashicorp/bionic64 virtualbox 1.0
            Would remove hashicorp/bionic64 virtualbox 1.0
            """);

        Assert.NotNull(preview);
        Assert.Equal(2, preview.Removed.Count);
    }

    /// <summary>
    /// A checkpoint alert, a malformed version's warning and a Vagrantfile's own output can come
    /// first, and none of them is a box.
    /// </summary>
    [Fact]
    public void IgnoresWhatVagrantPrintsBeforeTheReport()
    {
        var preview = VagrantPruneReport.TryRead(
            """
            ==> vagrant: A new version of Vagrant is available: 9.9.9 (installed version: 2.4.0)!

            ==> vagrant: Invalid version 'latest' for 'hashicorp/bionic64', ignoring...
            Would remove somebody else's line virtualbox 0.1
            box (virtualbox, 0.1)
            """ + "\n" + TwoBoxesOneSuperseded);

        Assert.NotNull(preview);
        Assert.Equal(2, preview.Kept.Count);
        Assert.All(preview.Removed, box => Assert.Equal("hashicorp/bionic64", box.Name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("The following boxes will be kept...\nhashicorp/bionic64 (virtualbox, 1.0.282)\n")]
    [InlineData("The following boxes will be kept...\nhashicorp/bionic64 (virtualbox, 1.0.282)\n\nChecking for older boxes...\n")]
    [InlineData("Checking for older boxes...\nWould remove hashicorp/bionic64 virtualbox 1.0.0\n")]
    public void ReadsAReportThatNeverFinishedAsNoReport(string output) =>
        Assert.Null(VagrantPruneReport.TryRead(output));

    /// <summary>Prune builds both lists from one table, so a removal it keeps nothing of is not its line.</summary>
    [Theory]
    [InlineData("Would remove generic/alpine38 virtualbox 1.0")]
    [InlineData("Would remove hashicorp/bionic64 libvirt 1.0")]
    [InlineData("Would remove hashicorp/bionic64 virtualbox 2.0")]
    public void ReadsARemovalWithNoNewerKeptVersionAsNoReport(string removal) =>
        Assert.Null(VagrantPruneReport.TryRead(
            "The following boxes will be kept...\nhashicorp/bionic64 (virtualbox, 2.0)\n\n"
            + $"Checking for older boxes...\n{removal}\n"));

    /// <summary>A version folder's name must be a RubyGems version before Vagrant lists the box at all.</summary>
    [Fact]
    public void ReadsAVersionVagrantCouldNotHaveListedAsNoReport() =>
        Assert.Null(VagrantPruneReport.TryRead(
            "The following boxes will be kept...\nhashicorp/bionic64 (virtualbox, 2.0)\n\n"
            + "Checking for older boxes...\nWould remove hashicorp/bionic64 virtualbox ..\\..\\x\n"));
}
