using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Network;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>The places Explore remembers between sessions: computers added by name, recent folders, and where the browser was left.</summary>
public sealed class ExploreLocationsTests
{
    [Fact]
    public void AddedComputersAreKeptAToZOnceEachCaseAside()
    {
        var locations = ExploreLocations.None.WithComputer("nas.test").WithComputer("atlas.test").WithComputer("NAS.test");

        Assert.Equal(["atlas.test", "nas.test"], locations.Computers);
        Assert.Equal(["nas.test"], locations.WithoutComputer("ATLAS.test").Computers);
    }

    /// <summary>A hand-edited entry that names no computer is dropped as the file is read.</summary>
    [Fact]
    public void AnEntryThatNamesNoComputerIsDropped()
    {
        var locations = ExploreLocations.None with { Computers = ["nas.test", "", @"\\nas.test\media", null!, "my nas"] };

        Assert.Equal(["nas.test"], locations.Computers);
    }

    /// <summary>
    /// A folder scanned becomes the most recent, once, and the oldest falls off past the number kept.
    /// A whole drive is not kept, because the drive box already offers every drive.
    /// </summary>
    [Fact]
    public void AFolderScannedIsTheMostRecentAndAWholeDriveIsNotKept()
    {
        var locations = ExploreLocations.None;

        for (var i = 0; i < ExploreLocations.RecentKept + 2; i++)
        {
            locations = locations.Scanned(new ExploreTarget(@"D:\", $@"D:\Folder{i}"));
        }

        locations = locations.Scanned(new ExploreTarget(@"D:\", @"d:\folder5"));
        locations = locations.Scanned(new ExploreTarget(@"E:\", null));

        Assert.Equal(ExploreLocations.RecentKept, locations.Recent.Count);
        Assert.Equal(@"d:\folder5", locations.Recent[0]);
        Assert.Equal(@"D:\Folder9", locations.Recent[1]);
        Assert.DoesNotContain(@"D:\Folder5", locations.Recent);
        Assert.DoesNotContain(@"E:\", locations.Recent);
        Assert.DoesNotContain(@"D:\Folder1", locations.Recent);
    }

    [Fact]
    public void LocationsHoldingTheSamePlacesAreEqual()
    {
        var one = ExploreLocations.None.WithComputer("nas.test").LeftAt(NetworkRoot.Added, @"\\nas.test\media");
        var two = ExploreLocations.None.WithComputer("nas.test").LeftAt(NetworkRoot.Added, @"\\nas.test\media");

        Assert.Equal(one, two);
        Assert.NotEqual(one, two.LeftAt(NetworkRoot.Network, @"\\nas.test\media"));
    }

    /// <summary>The added computers, the recent folders and where the browser was left survive a restart.</summary>
    [Fact]
    public void LocationsAreStoredAndReadBack()
    {
        using var temp = new TempDirectory();
        var store = new PreferenceStore(new FakeUserEnvironment(temp.Path));
        var locations = ExploreLocations.None
            .WithComputer("nas.test")
            .Scanned(new ExploreTarget(null, @"\\nas.test\media\Photos"))
            .LeftAt(NetworkRoot.Added, @"\\nas.test\media");

        Assert.True(store.Save(AppPreferences.Default with { Locations = locations }));

        Assert.Equal(locations, store.Load().Locations);
    }
}
