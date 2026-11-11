using Deguffer.Core.Exploring.Network;

namespace Deguffer.Core.Tests;

/// <summary>How a network place's path is built and read back, and what counts as a computer's name.</summary>
public sealed class NetworkNamesTests
{
    [Theory]
    [InlineData("nas", "nas")]
    [InlineData("nas.local", "nas.local")]
    [InlineData("192.0.2.10", "192.0.2.10")]
    [InlineData(@"  \\nas.test\  ", "nas.test")]
    [InlineData("fe80--1.ipv6-literal.net", "fe80--1.ipv6-literal.net")]
    public void AComputerIsTypedByNameOrAddress(string typed, string name) =>
        Assert.Equal(name, NetworkNames.ComputerName(typed));

    /// <summary>A path, a share, a space, a colon or a leading dot names no computer, so nothing is added.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(@"\\")]
    [InlineData(@"\\nas.test\media")]
    [InlineData("nas/media")]
    [InlineData("my nas")]
    [InlineData("fe80::1")]
    [InlineData(".nas")]
    [InlineData("nas?")]
    public void TextThatIsNotAComputerNameNamesNone(string typed) =>
        Assert.Null(NetworkNames.ComputerName(typed));

    [Fact]
    public void AComputerNameLongerThanAnyDnsNameNamesNone() =>
        Assert.Null(NetworkNames.ComputerName(new string('a', 254)));

    [Theory]
    [InlineData(@"\\nas.test", "nas.test", null)]
    [InlineData(@"\\nas.test\media", "nas.test", @"\\nas.test\media")]
    [InlineData(@"\\nas.test\media\Photos\2026", "nas.test", @"\\nas.test\media")]
    [InlineData(@"D:\Folder", null, null)]
    [InlineData(@"\\?\UNC\nas.test\media", null, null)]
    public void ASharePathIsReadBackIntoItsComputerAndShare(string path, string? server, string? share)
    {
        Assert.Equal(server, NetworkNames.ServerOf(path));
        Assert.Equal(share, NetworkNames.ShareOf(path));
    }

    [Theory]
    [InlineData(@"\\nas.test\media", @"\\nas.test\media", true)]
    [InlineData(@"\\nas.test\media", @"\\NAS.test\Media\Photos", true)]
    [InlineData(@"\\nas.test\media\", @"\\nas.test\media\Photos", true)]
    [InlineData(@"\\nas.test\media", @"\\nas.test\mediaroom", false)]
    [InlineData(@"\\nas.test\media\Photos", @"\\nas.test\media", false)]
    public void APlaceLeadsToItselfAndWhatIsInsideIt(string place, string target, bool leads) =>
        Assert.Equal(leads, NetworkNames.Leads(place, target));

    [Theory]
    [InlineData(@"\\nas.test\media\Photos", "Photos")]
    [InlineData(@"\\nas.test\media\", "media")]
    [InlineData(@"\\nas.test", "nas.test")]
    public void APlaceIsCalledByItsLastName(string path, string name) =>
        Assert.Equal(name, NetworkNames.LastName(path));
}
