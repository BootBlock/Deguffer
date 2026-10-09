using Deguffer.Core.Duplicates;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>§7.4's keeping rule: which copies a group can count on keeping, and which Explore's policy refuses.</summary>
public sealed class DuplicateKeepingTests : DuplicateMarkingScene
{
    /// <summary>
    /// The places a provider declares, with its name, reach the keeping rule through the protections
    /// a page builds, not only through a list a test hands in.
    /// </summary>
    [Fact]
    public async Task WhereStoragesCleansDeleteReachesTheKeepingRuleWithTheCleansName()
    {
        var cache = Path.Combine(_tree.Environment.LocalAppData, "Fake");
        var provider = new FakeCleanupProvider("fake") { Name = "Fake cache", Cleaned = [CleanedPlace.Whole(cache)] };
        var protections = await Protections(provider);

        Assert.Equal([new StorageClean(CleanedPlace.Whole(cache), "Fake cache")], await protections.StorageCleansAsync());

        var cached = Copy(Path.Combine(cache, "a.bin"));
        var marks = await MarksAsync(Result([cached, Copy(Path.Combine(Documents, "a.bin"))]), protections);

        Assert.Contains("Storage's 'Fake cache' clean", marks.Keeping.WhyNotKept(cached));
    }

    /// <summary>A drive is internal by its disks: rotational and solid state are, and a virtual disk, which can be detached, is not.</summary>
    [Theory]
    [InlineData(Rotational, StorageMedia.Rotational, true)]
    [InlineData(SolidState, StorageMedia.SolidState, true)]
    [InlineData(Virtual, StorageMedia.Virtual, false)]
    public void ACopyCountsAsKeptOutsideAReferenceOnlyOnAnInternalDrive(string root, StorageMedia media, bool kept)
    {
        var volume = new LocalVolume(root, DriveType.Fixed, VolumeReadiness.Ready);
        Assert.Equal(media, _media.Of(volume).Class);

        var marks = Marks([Copy(Path.Combine(root, "a.jpg"), volume: volume), Copy(Path.Combine(Documents, "a.jpg"))]);

        Assert.Equal(kept, marks.Keeping.WhyNotKept(Only(marks).Group.Files[0]) is null);
    }

    [Theory]
    [InlineData(LocationRole.Search)]
    [InlineData(LocationRole.Reference)]
    public void ACopyInTheTemporaryFolderNeverCountsAsKept(LocationRole role)
    {
        var marks = Marks([Copy(InTemp("a.jpg"), role: role), Copy(Path.Combine(Documents, "a.jpg"))]);

        Assert.Contains("temporary folder", marks.Keeping.WhyNotKept(Only(marks).Group.Files[0]));
    }

    [Theory]
    [InlineData(LocationRole.Search)]
    [InlineData(LocationRole.Reference)]
    public void ACopyWhereAStorageCleanDeletesNeverCountsAsKept(LocationRole role)
    {
        var cache = Path.Combine(_tree.Environment.LocalAppData, "npm-cache");
        _cleans.Add(new StorageClean(CleanedPlace.Whole(cache), "npm cache"));
        _cleans.Add(new StorageClean(CleanedPlace.FoldersNamed(Path.Combine(Documents, "Source"), ["node_modules"]), "Node.js packages"));

        var marks = Marks(
            [Copy(Path.Combine(cache, "_cacache", "a.tgz"), role: role), Copy(Path.Combine(Documents, "a.tgz"))],
            [Copy(Path.Combine(Documents, "Source", "app", "node_modules", "x", "b.js"), role: role), Copy(Path.Combine(Documents, "b.js"))]);

        Assert.All(marks.Groups, group => Assert.Contains("Storage's", marks.Keeping.WhyNotKept(group.Group.Files[0])));
        Assert.All(marks.Groups, group => Assert.Null(marks.Keeping.WhyNotKept(group.Group.Files[1])));
    }

    /// <summary>
    /// A clean's place named through a junction is the folder the junction leads to, which is the
    /// path a copy found there carries.
    /// </summary>
    [Fact]
    public void APlaceNamedThroughAJunctionHoldsTheCopiesAtTheFolderItLeadsTo()
    {
        var real = _tree.Folder("Caches", "npm-cache");
        var named = Path.Combine(_tree.Environment.LocalAppData, "npm-cache");
        Directory.CreateDirectory(_tree.Environment.LocalAppData);
        Junction.ToDirectory(named, real);
        _cleans.Add(new StorageClean(CleanedPlace.Whole(named), "npm cache"));

        var marks = Marks([Copy(Path.Combine(real, "a.tgz")), Copy(Path.Combine(Documents, "a.tgz"))]);

        Assert.Contains("Storage's", marks.Keeping.WhyNotKept(Only(marks).Group.Files[0]));
    }

    [Fact]
    public void ACopyOnAUsbDiskThatSaysItIsFixedCountsAsKeptOnlyInAReference()
    {
        var marks = Marks([OnUsb("a.jpg"), OnUsb("b.jpg", role: LocationRole.Reference), Copy(Path.Combine(Documents, "a.jpg"))]);
        var files = Only(marks).Group.Files;

        Assert.Equal(DriveType.Fixed, files[0].Volume.Kind);
        Assert.Contains("removable or USB", marks.Keeping.WhyNotKept(files[0]));
        Assert.Null(marks.Keeping.WhyNotKept(files[1]));
    }

    /// <summary>
    /// A disk on a bus that says nothing of its speed, and that would not say whether it incurs a seek
    /// penalty, is of no known kind: it may be a USB disk behind a bridge Windows did not report.
    /// </summary>
    [Fact]
    public void ACopyOnADriveOfNoKnownKindIsNotCountedOnOutsideAReference()
    {
        var unknown = new LocalVolume(Unknown, DriveType.Fixed, VolumeReadiness.Ready);
        var marks = Marks(
        [
            Copy(Path.Combine(Unknown, "a.jpg"), volume: unknown),
            Copy(Path.Combine(Unknown, "b.jpg"), role: LocationRole.Reference, volume: unknown),
            Copy(Path.Combine(Documents, "a.jpg")),
        ]);
        var files = Only(marks).Group.Files;

        Assert.Contains("what kind of drive", marks.Keeping.WhyNotKept(files[0]));
        Assert.Null(marks.Keeping.WhyNotKept(files[1]));
    }

    /// <summary>
    /// Storage's cloud row releases a file's local copy and deletes nothing, so it names no place, and
    /// a reference copy in a cloud folder it works in is still one a group can keep.
    /// </summary>
    [Fact]
    public async Task AReferenceCopyWhoseCloudFileStorageOnlyReleasesStillCounts()
    {
        var oneDrive = Path.Combine(_tree.Environment.UserProfile, "OneDrive");
        _cloud.Root("OneDrive!S-1!Personal", oneDrive, "OneDrive - Personal");
        var releases = new CloudLocalCopiesProvider(_cloud, _tree.Environment);
        _cleans.AddRange((await releases.CleanedPlacesAsync()).Select(place => new StorageClean(place, releases.Name)));

        var marks = Marks([Copy(Path.Combine(oneDrive, "a.jpg"), role: LocationRole.Reference), Copy(Path.Combine(Documents, "a.jpg"))]);

        Assert.Null(marks.Keeping.WhyNotKept(Only(marks).Group.Files[0]));
    }

    [Fact]
    public void ACopyInACloudFolderCountsAsKeptOnlyInAReference()
    {
        var oneDrive = Path.Combine(_tree.Environment.UserProfile, "OneDrive");
        _cloud.Root("OneDrive!S-1!Personal", oneDrive, "OneDrive - Personal");

        var marks = Marks(
        [
            Copy(Path.Combine(oneDrive, "a.jpg")),
            Copy(Path.Combine(oneDrive, "b.jpg"), role: LocationRole.Reference),
            Copy(Path.Combine(Documents, "a.jpg")),
        ]);
        var files = Only(marks).Group.Files;

        Assert.Contains("OneDrive - Personal", marks.Keeping.WhyNotKept(files[0]));
        Assert.Null(marks.Keeping.WhyNotKept(files[1]));
        Assert.NotNull(marks.Keeping.CloudFolderOf(files[1]));
        Assert.Null(marks.Keeping.CloudFolderOf(files[2]));
    }

    [Fact]
    public void NoCopyCountsAsOutsideACloudFolderWhenTheSyncRootsCannotBeRead()
    {
        _cloud.RefusesToListRoots = true;

        var marks = Marks([Copy(Path.Combine(Documents, "a.jpg")), Copy(Path.Combine(Downloads, "b.jpg"), role: LocationRole.Reference)]);
        var files = Only(marks).Group.Files;

        Assert.NotNull(marks.Keeping.WhyNotKept(files[0]));
        Assert.NotNull(marks.Keeping.CloudFolderOf(files[0]));
        Assert.Null(marks.Keeping.WhyNotKept(files[1]));
        Assert.Equal(0, marks.Run(new MarkingRule.KeepNewest()).Marked);
    }

    [Fact]
    public void AFileWithOneNameInTheTemporaryFolderAndAnotherInDocumentsCanBeKept()
    {
        var file = Copy(InTemp("a.jpg"), names: [InTemp("a.jpg"), Path.Combine(Documents, "a.jpg")]);
        var marks = Marks([file, Copy(Path.Combine(Downloads, "a.jpg"))]);

        Assert.Null(marks.Keeping.WhyNotKept(Only(marks).Group.Files[0]));
    }

    [Fact]
    public void EveryExploreRefusalApplies()
    {
        var inWindows = Copy(Path.Combine(_tree.System.WindowsDirectory, "System32", "a.dll"));
        var inProgramFiles = Copy(Path.Combine(_tree.System.ProgramFiles, "Vendor", "a.dll"));
        var otherAccount = Copy(Path.Combine(_tree.Users, "other", "Documents", "a.dll"));
        var mailStore = Copy(Path.Combine(Documents, "archive.pst"));
        var marks = Marks([inWindows, inProgramFiles, otherAccount, mailStore, Copy(Path.Combine(Downloads, "a.dll"))]);
        var policy = _tree.Policy();

        foreach (var refused in (DuplicateCandidate[])[inWindows, inProgramFiles, otherAccount, mailStore])
        {
            var verdict = policy.MayRemove(refused.Path);

            Assert.False(verdict.IsAllowed);
            Assert.Equal(verdict.Reason, Only(marks).Mark(refused, marks.Keeping));
            Assert.Equal(verdict.Reason, marks.Keeping.WhyNotKept(refused));
        }
    }

    [Fact]
    public void AToolRootsUnrecognisedChildIsRefused()
    {
        var gradle = Path.Combine(_tree.Environment.UserProfile, ".gradle");
        _policy = new ExploreActionPolicy(
            ProtectedRegions.For(_tree.System, _tree.Environment),
            [ToolRoot.Folders(gradle, "Gradle keeps its settings here.", name => name == "caches")],
            _tree.Volumes);
        var settings = Copy(Path.Combine(gradle, "init.d", "a.gradle"));
        var cached = Copy(Path.Combine(gradle, "caches", "a.gradle"));
        var marks = Marks([settings, cached, Copy(Path.Combine(Documents, "a.gradle"))]);

        Assert.Contains("not something Deguffer recognises", Only(marks).Mark(settings, marks.Keeping));
        Assert.Null(Only(marks).Mark(cached, marks.Keeping));
    }

    /// <summary>
    /// What a page lists beside a copy: why it may not be marked, and why it may not be kept where
    /// that says something more. A refused copy is not kept for the reason it is refused, so that
    /// reason is listed once; a reference copy may be kept, and a copy in the temporary folder may be
    /// marked.
    /// </summary>
    [Fact]
    public void ACopysStandingListsEachReasonOnce()
    {
        var reference = Copy(Path.Combine(Documents, "a.jpg"), role: LocationRole.Reference);
        var online = Copy(Path.Combine(Documents, "b.jpg"), storage: FileStorage.CloudOnly);
        var temporary = Copy(InTemp("c.jpg"));
        var marks = Marks([reference, online, temporary, Copy(Path.Combine(Downloads, "a.jpg"))]);

        Assert.Equal(new CopyStanding(CopyRefusals.WhyNeverMarked(reference), null), marks.Keeping.Standing(reference));
        Assert.Equal(new CopyStanding(marks.Keeping.Refusals.WhyRefused(online), null), marks.Keeping.Standing(online));
        Assert.NotNull(marks.Keeping.Standing(online).WhyNotMarked);

        var standing = marks.Keeping.Standing(temporary);
        Assert.Null(standing.WhyNotMarked);
        Assert.Contains("temporary folder", standing.WhyNotKept, StringComparison.Ordinal);
    }
}
