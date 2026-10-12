using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which folder's shapes hold a shape, what it is a share of, and where the keyboard lands after
/// going up: for a folder drawn alone, and for the root of a volume drawn beside its free space.
/// </summary>
public sealed class MapFoldersTests
{
    private static readonly ExploreTree Tree = Build();

    private static int Root => Tree.RootNode;

    private static int Users => Named("Users");

    private static int Testuser => Named("testuser");

    private static int Notes => Named("notes.txt");

    [Fact]
    public void WhatTheDrawnFolderHoldsIsAtTheTop()
    {
        Assert.Null(MapFolders.Of(Tree, Root, beside: false, Users));
        Assert.Equal(Users, MapFolders.Of(Tree, Root, beside: false, Testuser));
        Assert.Null(MapFolders.Of(Tree, Users, beside: false, Testuser));
    }

    /// <summary>Beside a volume the root is at the top, and what it holds is the root's.</summary>
    [Fact]
    public void BesideAVolumeTheRootIsAtTheTopAndHoldsWhatIsInIt()
    {
        Assert.Null(MapFolders.Of(Tree, Root, beside: true, Root));
        Assert.Equal(Root, MapFolders.Of(Tree, Root, beside: true, Users));
        Assert.Equal(Users, MapFolders.Of(Tree, Root, beside: true, Testuser));
    }

    [Fact]
    public void AShapeIsAShareOfItsFolderOrOfTheDrawnFolderAtTheTop()
    {
        Assert.Equal((Tree.SizeOf(Users), (int?)Users), MapFolders.ShareBasis(Tree, Root, beside: false, Users, []));
        Assert.Equal((Tree.SizeOf(Root), (int?)Root), MapFolders.ShareBasis(Tree, Root, beside: false, folder: null, []));
    }

    /// <summary>Beside a volume, the top of the picture is the whole drive, which no node names.</summary>
    [Fact]
    public void BesideAVolumeTheTopIsAShareOfTheWholeDrive()
    {
        IReadOnlyList<MapShape> top =
        [
            MapShape.Of(Root, Tree.SizeOf(Root), MapFrame.Whole),
            MapShape.Of(ExploreTile.FreeSpace, 7_000, MapFrame.Whole),
        ];

        Assert.Equal((Tree.SizeOf(Root) + 7_000, (int?)null), MapFolders.ShareBasis(Tree, Root, beside: true, folder: null, top));
    }

    /// <summary>
    /// Coming up, the keyboard is on the shape of the folder it came out of, however far down it was;
    /// going down, or anywhere else, it starts afresh.
    /// </summary>
    [Fact]
    public void ComingUpTheKeyboardIsOnTheFolderItCameOutOf()
    {
        Assert.Equal(Users, MapFolders.CameUpFrom(Tree, Testuser, Root, beside: false));
        Assert.Equal(Users, MapFolders.CameUpFrom(Tree, Notes, Root, beside: false));
        Assert.Equal(Testuser, MapFolders.CameUpFrom(Tree, Notes, Users, beside: false));
        Assert.Null(MapFolders.CameUpFrom(Tree, Root, Users, beside: false));
        Assert.Null(MapFolders.CameUpFrom(Tree, Users, Named("Windows"), beside: false));
    }

    /// <summary>The root drawn again beside its volume keeps the keyboard on the root, and opened out of it does not.</summary>
    [Fact]
    public void TheRootDrawnBesideItsVolumeKeepsTheKeyboardOnIt()
    {
        Assert.Equal(Root, MapFolders.CameUpFrom(Tree, Root, Root, beside: true));
        Assert.Null(MapFolders.CameUpFrom(Tree, Root, Root, beside: false));
    }

    /// <summary>
    /// A screen reader is given the shapes of the keyboard's folder and of every folder holding it, and
    /// of a folder it expanded; any other folder's shape is collapsed.
    /// </summary>
    [Fact]
    public void TheKeyboardsPathAndWhatTheReaderExpandedAreListed()
    {
        var windows = Named("Windows");

        Assert.True(MapFolders.Lists(Tree, Root, beside: false, Testuser, keyboard: Testuser, new HashSet<int>()));
        Assert.True(MapFolders.Lists(Tree, Root, beside: false, Users, keyboard: Testuser, new HashSet<int>()));
        Assert.False(MapFolders.Lists(Tree, Root, beside: false, windows, keyboard: Testuser, new HashSet<int>()));
        Assert.False(MapFolders.Lists(Tree, Root, beside: false, Users, keyboard: null, new HashSet<int>()));
        Assert.True(MapFolders.Lists(Tree, Root, beside: false, windows, keyboard: null, new HashSet<int> { windows }));
    }

    /// <summary>
    /// Beside a volume the root is a shape at the top like any other, listed on the keyboard's way up;
    /// and a keyboard's folder outside the picture lists nothing in it, rather than going on for ever.
    /// </summary>
    [Fact]
    public void BesideAVolumeTheRootIsOnTheKeyboardsPath()
    {
        Assert.True(MapFolders.Lists(Tree, Root, beside: true, Root, keyboard: Testuser, new HashSet<int>()));
        Assert.False(MapFolders.Lists(Tree, Root, beside: false, Root, keyboard: Testuser, new HashSet<int>()));
        Assert.False(MapFolders.Lists(Tree, Users, beside: false, Testuser, keyboard: Named("Windows"), new HashSet<int>()));
    }

    private static int Named(string name) =>
        Enumerable.Range(0, Tree.NodeCount).First(node => Tree.NameOf(node) == name);

    private static ExploreTree Build()
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var users = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [
                new ExploreChild("Users", IsDirectory: true, IsLink: false, Size: 0),
                new ExploreChild("Windows", IsDirectory: true, IsLink: false, Size: 0),
            ]);
        var testuser = builder.AddChildren(users, [new ExploreChild("testuser", IsDirectory: true, IsLink: false, Size: 0)]);

        builder.AddChildren(testuser, [new ExploreChild("notes.txt", IsDirectory: false, IsLink: false, Size: 500)]);
        builder.AddChildren(users + 1, [new ExploreChild("win.ini", IsDirectory: false, IsLink: false, Size: 100)]);

        return builder.Build(ExploreChildOrder.BySize);
    }
}
