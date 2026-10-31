using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a screen reader says of a shape of a drawing: whose share it is, in what the page says of the
/// tree, at the top of a folder's picture and of a volume's, and for a block; and that what the page
/// does not say is left unsaid.
/// </summary>
public sealed class MapShapeTextDrawingTests
{
    private const int Width = 800;

    private const int Height = 600;

    private static readonly ExploreTree Tree = Build();

    private static int Users => Named("Users");

    private static int Documents => Named("Documents");

    [Fact]
    public void AShapeInAFolderIsAShareOfTheFolder() =>
        Assert.Equal(
            $"Documents, {FreeSpace.Format(600)}, 60% of Users",
            MapShapeText.Name(Alone(), Users, ShapeOf(Alone(), Users, Documents), Facts));

    [Fact]
    public void AShapeAtTheTopIsAShareOfTheDrawnFolder() =>
        Assert.Equal(
            $"Users, {FreeSpace.Format(1000)}, 50% of {Tree.NameOf(Tree.RootNode)}",
            MapShapeText.Name(Alone(), null, ShapeOf(Alone(), null, Users), Facts));

    /// <summary>Beside a volume the top of the picture is the whole drive, which no node names.</summary>
    [Fact]
    public void BesideAVolumeTheTopIsAShareOfTheDrive()
    {
        var drawing = Beside();

        Assert.Equal(
            $"{MapBlock.FreeSpace.Name}, {FreeSpace.Format(1000)}, 25% of the drive",
            MapShapeText.Name(drawing, null, ShapeOf(drawing, null, ExploreTile.FreeSpace), Facts));
    }

    /// <summary>A share of a folder the page says nothing of is not said, rather than said to be of the drive.</summary>
    [Fact]
    public void AShareOfAFolderThePageSaysNothingOfIsLeftOut() =>
        Assert.Equal(
            $"Documents, {FreeSpace.Format(600)}",
            MapShapeText.Name(Alone(), Users, ShapeOf(Alone(), Users, Documents), node => node == Users ? null : Facts(node)));

    [Fact]
    public void AShapeThePageSaysNothingOfHasNoName() =>
        Assert.Equal(
            string.Empty,
            MapShapeText.Name(Alone(), Users, ShapeOf(Alone(), Users, Documents), node => node == Documents ? null : Facts(node)));

    [Fact]
    public void ASizeThatIsALowerBoundIsSaidToBeOne() =>
        Assert.Equal(
            $"Documents, {FreeSpace.Format(600)}, 60% of Users{ExploreNodeText.Unread}",
            MapShapeText.Name(
                Alone(), Users, ShapeOf(Alone(), Users, Documents),
                node => node == Documents ? Facts(node)! with { LowerBound = true } : Facts(node)));

    /// <summary>A block says what it stands for, and is never asked about, being no node of the tree.</summary>
    [Fact]
    public void ABlockSaysWhatItStandsFor()
    {
        var drawing = Beside();

        Assert.Equal(
            MapBlock.FreeSpace.Help,
            MapShapeText.Help(ShapeOf(drawing, null, ExploreTile.FreeSpace), Unasked<MapShapeFacts>, Unasked<ExploreVerdict>));
    }

    [Fact]
    public void ANodeSaysWhatItIsAndWhetherItCanBeRemoved() =>
        Assert.Equal(
            "Folder. Enter opens it. It cannot be removed. Kept.",
            MapShapeText.Help(ShapeOf(Alone(), null, Users), Facts, _ => ExploreVerdict.Refuse("Kept.")));

    /// <summary>Whether a node may be removed asks the machine, so it is not asked where nothing is said of the node.</summary>
    [Fact]
    public void ANodeThePageSaysNothingOfHasNoHelpAndIsNotJudged() =>
        Assert.Equal(
            string.Empty,
            MapShapeText.Help(ShapeOf(Alone(), null, Users), _ => null, Unasked<ExploreVerdict>));

    private static MapShapeFacts? Facts(int node) =>
        new(Tree.NameOf(node), Tree.IsDirectory(node) ? "Folder" : "File", Opens: Tree.IsDirectory(node), LowerBound: false);

    private static T? Unasked<T>(int node)
        where T : class =>
        throw new InvalidOperationException($"Asked about {node}.");

    private static MapShape ShapeOf(ExploreSurface drawing, int? folder, int node) =>
        drawing.Shapes(folder).Single(shape => shape.Node == node);

    private static ExploreSurface Alone() => Drawn(VolumeSpace.None);

    private static ExploreSurface Beside()
    {
        var drawing = Drawn(new VolumeSpace(TotalBytes: 4_000, FreeBytes: 1_000));

        Assert.True(drawing.HasVolumeBeside);

        return drawing;
    }

    private static ExploreSurface Drawn(VolumeSpace volume) => ExploreSurface.Create(
        Tree, Tree.RootNode, ExploreView.Treemap, Width, Height, 1, 1,
        ShapeColours.ByBranch(ExploreScheme.Standard), ExploreSpacing.Comfortable, volume);

    private static int Named(string name) =>
        Enumerable.Range(0, Tree.NodeCount).First(node => Tree.NameOf(node) == name);

    /// <summary>A drive of 2,000 bytes: Users, which holds Documents and a file, beside a file as big as Users.</summary>
    private static ExploreTree Build()
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var users = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [
                new ExploreChild("Users", IsDirectory: true, IsLink: false, Size: 0),
                new ExploreChild("pagefile.sys", IsDirectory: false, IsLink: false, Size: 1_000),
            ]);

        var documents = builder.AddChildren(
            users,
            [
                new ExploreChild("Documents", IsDirectory: true, IsLink: false, Size: 0),
                new ExploreChild("notes.txt", IsDirectory: false, IsLink: false, Size: 400),
            ]);

        builder.AddChildren(documents, [new ExploreChild("report.pdf", IsDirectory: false, IsLink: false, Size: 600)]);

        return builder.Build(ExploreChildOrder.BySize);
    }
}
