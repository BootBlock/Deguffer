using Deguffer.Core.Duplicates;
using Deguffer.Core.Exploring;

namespace Deguffer.Core.Tests;

/// <summary>Whether a drive or folder the user picked can join the Duplicates page's list (§7.4).</summary>
public sealed class LocationChoiceTests
{
    private static readonly SearchLocation[] Listed = [new(@"C:\Users\testuser\Photos")];

    [Fact]
    public void AFolderOnADiskJoins() =>
        Assert.Null(LocationChoice.WhyNotAdded(Listed, @"C:\Users\testuser\Downloads"));

    /// <summary>The picker can hand back a library or a device, which is not a folder on a disk.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("Photos")]
    [InlineData(@"::{20D04FE0-3AEA-1069-A2D8-08002B30309D}")]
    public void WhatIsNotAFolderOnADiskIsRefused(string picked) =>
        Assert.Equal(LocationChoice.NotOnDisk, LocationChoice.WhyNotAdded(Listed, picked));

    /// <summary>
    /// The same path twice is refused, and a path differing only in case is not, because a
    /// case-sensitive folder can hold both and they are two places.
    /// </summary>
    [Fact]
    public void OnlyTheSamePathCaseAndAllIsAlreadyListed()
    {
        Assert.Equal(LocationChoice.AlreadyListed, LocationChoice.WhyNotAdded(Listed, @"C:\Users\testuser\Photos"));
        Assert.Null(LocationChoice.WhyNotAdded(Listed, @"C:\Users\testuser\photos"));
    }

    /// <summary>A drive Explore refuses to scan, a cloud drive among them, is refused with Explore's reason.</summary>
    [Fact]
    public void ADriveExploreRefusesIsRefusedForItsReason()
    {
        var cloud = new DriveChoice(@"G:\", "Cloud", null, null, DriveChoice.RemoteStorageRefusal);

        Assert.Equal(DriveChoice.RemoteStorageRefusal, LocationChoice.WhyNotAdded([], cloud));
        Assert.Null(LocationChoice.WhyNotAdded([], new DriveChoice(@"D:\", "Data", null, null)));
        Assert.Equal(LocationChoice.AlreadyListed, LocationChoice.WhyNotAdded([new SearchLocation(@"D:\")], new DriveChoice(@"D:\", "Data", null, null)));
    }
}
