using Deguffer.Core.Safety;
using Deguffer.Testing;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Tests;

/// <summary>
/// <see cref="FileInformation"/>: what Windows says through a handle opened for attributes alone.
/// </summary>
public sealed class FileInformationTests
{
    /// <summary>
    /// §6.3, asserted by the form of the path that reaches <c>CreateFileW</c>. A folder deeper than
    /// <c>MAX_PATH</c> opens on this machine with or without the extended prefix, because Windows
    /// allows long paths here, so whether it was found proves nothing about the prefix; the path
    /// handed to Windows does.
    /// </summary>
    [Fact]
    public void TheHandleIsOpenedOnThePathInItsExtendedForm()
    {
        using var temp = new TempDirectory();
        var deep = temp.Path;

        while (deep.Length <= 300)
        {
            deep = Path.Combine(deep, new string('d', 40));
        }

        Directory.CreateDirectory(LongPath.Extended(deep));
        List<string> opened = [];
        var information = new FileInformation(
            (path, use) =>
            {
                opened.Add(path);
                return FileInformation.Open(path, use);
            },
            FileInformation.ReadIdentity);

        var final = information.FinalPath(deep);

        Assert.Equal(LongPath.Extended(deep), Assert.Single(opened));
        Assert.StartsWith(@"\\?\", opened[0], StringComparison.Ordinal);
        Assert.EndsWith(deep[temp.Path.Length..], LongPath.Display(final!), StringComparison.Ordinal);
    }

    [Fact]
    public void ALinkOnTheWayToAFolderIsFollowed()
    {
        using var temp = new TempDirectory();
        var target = temp.CreateDirectory("Target", "Inner");
        Junction.ToDirectory(Path.Combine(temp.Path, "Link"), Path.Combine(temp.Path, "Target"));

        var final = LongPath.Display(FileInformation.Default.FinalPath(Path.Combine(temp.Path, "Link", "Inner"))!);

        Assert.Equal(LongPath.Display(FileInformation.Default.FinalPath(target)!), final);
        Assert.EndsWith(Path.Combine("Target", "Inner"), final, StringComparison.Ordinal);
    }

    /// <summary>No answer is not an answer of "absent": the caller learns only that nothing was had.</summary>
    [Fact]
    public void APathWindowsWillNotOpenHasNoFinalPath()
    {
        using var temp = new TempDirectory();

        Assert.Null(FileInformation.Default.FinalPath(Path.Combine(temp.Path, "Nowhere")));
    }

    /// <summary>
    /// The route is asked of the folder a location leads to: a volume mounted in a folder is a link on
    /// the volume holding it, and asked as the link, the answer would be that volume's. A junction
    /// stands in for the mount here, with identities answered only for the folder it leads to.
    /// </summary>
    [Fact]
    public void TheRouteIsAskedOfTheFolderALinkLeadsTo()
    {
        using var temp = new TempDirectory();
        var target = temp.CreateDirectory("Target");
        Junction.ToDirectory(Path.Combine(temp.Path, "Link"), target);
        var targetFinal = FileInformation.Default.FinalPath(target);
        var onlyTheTarget = new FileInformation(
            FileInformation.Open,
            (SafeFileHandle handle, IdentityRoute route, out FileIdentity identity) =>
            {
                identity = default;
                return FileInformation.FinalPathOf(handle) == targetFinal;
            });

        Assert.Equal(IdentityRoute.FileId, onlyTheTarget.IdentityRouteOf(Path.Combine(temp.Path, "Link")));
    }

    /// <summary>A volume that answers the newer call is identified by it, never by the older one first.</summary>
    [Fact]
    public void AVolumeThatAnswersTheNewerCallIsIdentifiedByIt()
    {
        using var temp = new TempDirectory();

        Assert.Equal(IdentityRoute.FileId, FileInformation.Default.IdentityRouteOf(temp.Path));
    }

    /// <summary>
    /// §6.3 for the folder a route is asked of and the path a file's names are listed by, asserted by
    /// the form of the path, as above.
    /// </summary>
    [Fact]
    public void AFolderAndAFilesNamesAreAskedOnThePathInItsExtendedForm()
    {
        using var temp = new TempDirectory();
        var deep = temp.Path;

        while (deep.Length <= 300)
        {
            deep = Path.Combine(deep, new string('d', 40));
        }

        Directory.CreateDirectory(LongPath.Extended(deep));
        var file = Path.Combine(deep, "a.bin");
        File.WriteAllBytes(LongPath.Extended(file), new byte[7]);
        List<string> opened = [];
        List<string> listed = [];
        var information = new FileInformation(
            (path, use) =>
            {
                opened.Add(path);
                return FileInformation.Open(path, use);
            },
            FileInformation.ReadIdentity,
            path =>
            {
                listed.Add(path);
                return FileInformation.ListNames(path);
            });

        Assert.Equal(IdentityRoute.FileId, information.IdentityRouteOf(deep));
        Assert.Equal([LongPath.Display(FileInformation.Default.FinalPath(file)!)], information.NamesOf(file));
        Assert.Equal(LongPath.Extended(deep), Assert.Single(opened));
        Assert.Equal(LongPath.Extended(file), Assert.Single(listed));
    }

    /// <summary>§6.3 for the handle a file is described through, asserted by the form of the path, as above.</summary>
    [Fact]
    public void AFileIsDescribedOnThePathInItsExtendedForm()
    {
        using var temp = new TempDirectory();
        var deep = temp.Path;

        while (deep.Length <= 300)
        {
            deep = Path.Combine(deep, new string('d', 40));
        }

        Directory.CreateDirectory(LongPath.Extended(deep));
        var file = Path.Combine(deep, "a.bin");
        File.WriteAllBytes(LongPath.Extended(file), new byte[7]);
        List<string> opened = [];
        var information = new FileInformation(
            (path, use) =>
            {
                opened.Add(path);
                return FileInformation.Open(path, use);
            },
            FileInformation.ReadIdentity);

        var reading = information.Describe(file, IdentityRoute.FileId);

        Assert.Equal(LongPath.Extended(file), Assert.Single(opened));
        Assert.Equal(FileReadingResult.Identified, reading.Result);
        Assert.Equal(7, reading.Description!.Length);
    }

    [Fact]
    public void AFileThatIsNotThereIsGone()
    {
        using var temp = new TempDirectory();

        Assert.Equal(
            FileReadingResult.Gone,
            FileInformation.Default.Describe(Path.Combine(temp.Path, "Nowhere", "a.bin"), IdentityRoute.FileId).Result);
    }

    /// <summary>Two names of one file are one identity, and the file says it has both.</summary>
    [Fact]
    public void TwoNamesOfOneFileShareItsIdentityAndAreBothListed()
    {
        using var temp = new TempDirectory();
        var first = temp.CreateFile(5, "a.bin");
        var second = HardLink.To(first, Path.Combine(temp.Path, "Other", "b.bin"));

        var one = FileInformation.Default.Describe(first, IdentityRoute.FileId).Description!;
        var other = FileInformation.Default.Describe(second, IdentityRoute.FileId).Description!;

        Assert.Equal(one.Identity, other.Identity);
        Assert.Equal(2, one.Names);
        Assert.Equal(
            new[] { first, second }.Select(name => LongPath.Display(FileInformation.Default.FinalPath(name)!)).Order(StringComparer.Ordinal),
            FileInformation.Default.NamesOf(first)!.Order(StringComparer.Ordinal));
    }
}
