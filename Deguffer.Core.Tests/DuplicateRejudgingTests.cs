using Deguffer.Core.Duplicates;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>§7.4's "it asks again immediately before removal": the confirmation judges the marks against the machine as it is then, keeping every mark.</summary>
public sealed class DuplicateRejudgingTests : DuplicateMarkingScene
{
    /// <summary>
    /// A mark made while its group could keep another copy is judged again at the confirmation, as
    /// the machine is then: here the unmarked copy's folder became the temporary folder after the mark
    /// was made, so the group would keep only a copy that can go without anyone choosing it to.
    /// </summary>
    [Fact]
    public async Task AConfirmationJudgesTheMarksAgainAndKeepsNoneThatWouldLeaveNothingToKeep()
    {
        DuplicateCandidate[] files = [Copy(Path.Combine(Documents, "a.jpg")), Copy(Path.Combine(Downloads, "a.jpg"))];
        var marks = Marks(files);
        var group = Only(marks);
        Assert.Null(group.Mark(files[0], marks.Keeping));
        Assert.Equal([files[0]], group.Standing(marks.Keeping));

        _tree.Environment.WithTempPath(Downloads);
        var confirmation = await RemovalConfirmation.ForAsync(marks, await Protections(), ExploreRemovalMode.RecycleBin, _ => null);

        Assert.True(group.IsMarked(files[0]));
        Assert.Empty(confirmation.Copies);
        Assert.Equal(0, confirmation.Space);
        Assert.Contains("temporary folder", marks.Keeping.WhyNotKept(files[1]));
    }

    /// <summary>
    /// Where Storage's cleans delete is asked of the providers again at the confirmation, through the
    /// protections built for it, so a place a clean names after the marks were made counts.
    /// </summary>
    [Fact]
    public async Task AConfirmationAsksStorageAgainWhereItsCleansDelete()
    {
        DuplicateCandidate[] files = [Copy(Path.Combine(Documents, "a.jpg")), Copy(Path.Combine(Downloads, "a.jpg"))];
        var provider = new FakeCleanupProvider("downloads") { Name = "Old downloads" };
        var marks = await DuplicateMarks.ForAsync(Result(files), await Protections(provider), _tree.Environment, _cloud, _tree.Volumes, _media);
        var group = Only(marks);
        Assert.Null(group.Mark(files[0], marks.Keeping));

        provider.Cleaned = [CleanedPlace.Whole(Downloads)];
        var confirmation = await RemovalConfirmation.ForAsync(marks, await Protections(provider), ExploreRemovalMode.RecycleBin, _ => null);

        Assert.Empty(confirmation.Copies);
        Assert.Contains("'Old downloads'", marks.Keeping.WhyNotKept(files[1]));
    }

    /// <summary>
    /// Two copies, one marked through the route a page takes, whose unmarked copy is then made one a
    /// group cannot count on by <paramref name="change"/>; the confirmation built afterwards.
    /// </summary>
    private async Task<(RemovalConfirmation Confirmation, DuplicateMarks Marks, DuplicateCandidate Kept)> ConfirmAfter(
        Action change, FakeCleanupProvider? provider = null)
    {
        provider ??= new FakeCleanupProvider("none");
        DuplicateCandidate[] files = [Copy(Path.Combine(Documents, "a.dll")), Copy(Path.Combine(Downloads, "b", "a.dll"))];
        var marks = await DuplicateMarks.ForAsync(Result(files), await Protections(provider), _tree.Environment, _cloud, _tree.Volumes, _media);
        Assert.Null(Only(marks).Mark(files[0], marks.Keeping));

        change();
        var confirmation = await RemovalConfirmation.ForAsync(marks, await Protections(provider), ExploreRemovalMode.RecycleBin, _ => null);

        return (confirmation, marks, files[1]);
    }

    /// <summary>A program installed where the unmarked copy is, after the marks, refuses it at the confirmation.</summary>
    [Fact]
    public async Task AConfirmationReadsTheInstalledProgramsAgain()
    {
        var (confirmation, marks, kept) = await ConfirmAfter(() => _tree.Registry.With(
            InstalledApps.UninstallScope.Machine64, "Tool", ("DisplayName", "Tool"), ("InstallLocation", Path.Combine(Downloads, "b"))));

        Assert.Empty(confirmation.Copies);
        Assert.Contains("'Tool' is installed here", marks.Keeping.Refusals.WhyRefused(kept));
    }

    /// <summary>
    /// A program folder the search knew stays one at the confirmation even where Windows will not
    /// read the list of programs then, because an unread list names nothing.
    /// </summary>
    [Fact]
    public async Task AProgramFolderTheSearchKnewStaysOneWhenTheListCannotBeReadAgain()
    {
        var tool = Path.Combine(Downloads, "b");
        _programs.Add(new ProgramFolder(tool, ReachedFolder.At(tool, _tree.Volumes), "Tool", Final: null));
        DuplicateCandidate[] files = [Copy(Path.Combine(Documents, "a.dll")), Copy(Path.Combine(tool, "a.dll"))];
        var marks = await DuplicateMarks.ForAsync(Result(files), await Protections(), _tree.Environment, _cloud, _tree.Volumes, _media);

        _tree.Registry.Refusing(InstalledApps.UninstallScope.Machine64);
        await marks.RejudgeAsync(await Protections());

        Assert.Contains("'Tool' is installed here", marks.Keeping.Refusals.WhyRefused(files[1]));
    }

    /// <summary>
    /// A disk moved from an internal bay into a USB dock keeps its volume and every file ID on it, so
    /// only its bus, asked again at the confirmation, says the copy on it can now be unplugged.
    /// </summary>
    [Fact]
    public async Task AConfirmationAsksEachDriveWhatItsDisksAreAgain()
    {
        var (confirmation, marks, kept) = await ConfirmAfter(() => _queries.Disk(0, bus: 0x07, seekPenalty: null));

        Assert.Empty(confirmation.Copies);
        Assert.Contains("removable or USB", marks.Keeping.WhyNotKept(kept));
    }

    /// <summary>A tool root declared after the marks, whose unrecognised child the unmarked copy is, refuses it at the confirmation.</summary>
    [Fact]
    public async Task AConfirmationAsksExplorePolicyAgain()
    {
        var provider = new FakeCleanupProvider("tool");

        var (confirmation, marks, kept) = await ConfirmAfter(
            () => provider.ToolRoots = [ToolRoot.Folders(Downloads, "A tool keeps its settings here.", _ => false)], provider);

        Assert.Empty(confirmation.Copies);
        Assert.Contains("not something Deguffer recognises", marks.Keeping.Refusals.WhyRefused(kept));
    }

    /// <summary>A folder Windows lists as a sync root after the marks holds the unmarked copy in a cloud folder at the confirmation.</summary>
    [Fact]
    public async Task AConfirmationListsTheCloudFoldersAgain()
    {
        var (confirmation, marks, kept) = await ConfirmAfter(() => _cloud.Root("OneDrive!S-1!Personal", Downloads, "OneDrive - Personal"));

        Assert.Empty(confirmation.Copies);
        Assert.Contains("OneDrive - Personal", marks.Keeping.WhyNotKept(kept));
    }
}
