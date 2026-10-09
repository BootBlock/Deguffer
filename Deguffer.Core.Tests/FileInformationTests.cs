using Deguffer.Core.Safety;
using Deguffer.Testing;

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
        var information = new FileInformation(path =>
        {
            opened.Add(path);
            return FileInformation.OpenToResolve(path);
        });

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
}
