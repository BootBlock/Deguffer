using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// Every folder owns an arc of the hue circle inside its parent's, so a reader who knows a
/// top-level folder's colour knows the colour of everything in it, and two siblings are told apart
/// by their own arcs of it.
/// </summary>
public sealed class BranchHuesTests
{
    [Fact]
    public void TheRootOwnsTheWholeCircle()
    {
        var tree = Nested();

        Assert.Equal(BranchHue.Whole, new BranchHues(tree, tree.RootNode).Of(tree.RootNode));
    }

    /// <summary>The paper's rule, and the reason for the scheme: descendants stay in the branch's arc.</summary>
    [Fact]
    public void EveryDescendantsHueIsInsideItsTopLevelFoldersArc()
    {
        var tree = Nested();
        var hues = new BranchHues(tree, tree.RootNode);

        foreach (var branch in tree.ChildrenOf(tree.RootNode))
        {
            var arc = hues.Of(branch);

            foreach (var descendant in Below(tree, branch))
            {
                var hue = hues.Of(descendant).Centre;

                Assert.InRange(hue, arc.From, arc.From + arc.Sweep);
            }
        }
    }

    [Fact]
    public void SiblingsArcsDoNotOverlap()
    {
        var tree = Nested();
        var hues = new BranchHues(tree, tree.RootNode);

        var arcs = tree.ChildrenOf(tree.RootNode).ToArray().Select(hues.Of).OrderBy(arc => arc.From).ToList();

        for (var i = 1; i < arcs.Count; i++)
        {
            Assert.True(arcs[i].From >= arcs[i - 1].From + arcs[i - 1].Sweep, "two siblings share part of the circle");
        }
    }

    /// <summary>
    /// The two largest siblings are neighbours on the screen, so they are not neighbours on the
    /// circle: they sit about half the parent's arc apart.
    /// </summary>
    [Fact]
    public void TheTwoLargestSiblingsAreFarApartOnTheCircle()
    {
        var tree = Nested();
        var hues = new BranchHues(tree, tree.RootNode);
        var children = tree.ChildrenOf(tree.RootNode);

        var apart = Math.Abs(hues.Of(children[0]).Centre - hues.Of(children[1]).Centre);

        Assert.True(apart >= 120, $"the two largest branches are only {apart:F0} degrees apart");
    }

    /// <summary>Neighbouring siblings alternate a step of lightness, which survives a colour-vision deficiency.</summary>
    [Fact]
    public void NeighbouringSiblingsAlternateTheLift()
    {
        var tree = Nested();
        var hues = new BranchHues(tree, tree.RootNode);
        var children = tree.ChildrenOf(tree.RootNode);

        for (var i = 1; i < children.Length; i++)
        {
            Assert.NotEqual(hues.Of(children[i - 1]).Lifted, hues.Of(children[i]).Lifted);
        }
    }

    /// <summary>
    /// Measured from the drawing's root, so an opened folder owns the whole circle and its children
    /// spread across all of it rather than inside the arc it had when seen from above.
    /// </summary>
    [Fact]
    public void AnOpenedFolderOwnsTheWholeCircle()
    {
        var tree = Nested();
        var branch = tree.ChildrenOf(tree.RootNode)[0];

        var fromAbove = new BranchHues(tree, tree.RootNode).Of(tree.ChildrenOf(branch)[0]);
        var opened = new BranchHues(tree, branch).Of(tree.ChildrenOf(branch)[0]);

        Assert.Equal(BranchHue.Whole, new BranchHues(tree, branch).Of(branch));
        Assert.True(opened.Sweep > fromAbove.Sweep);
    }

    /// <summary>
    /// A directory can hold any number of children. They share the arc however many there are,
    /// rather than wrapping round to the colours of another branch.
    /// </summary>
    [Fact]
    public void AThousandSiblingsStayInsideTheirParentsArc()
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var folders = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [
                new ExploreChild("a", IsDirectory: true, IsLink: false, Size: 0),
                new ExploreChild("b", IsDirectory: true, IsLink: false, Size: 0),
            ]);
        builder.AddChildren(
            folders,
            [.. Enumerable.Range(0, 1000).Select(i => new ExploreChild($"f{i}", IsDirectory: false, IsLink: false, Size: 10))]);
        builder.AddChildren(folders + 1, [new ExploreChild("g", IsDirectory: false, IsLink: false, Size: 5)]);
        var tree = builder.Build(ExploreChildOrder.BySize);

        var hues = new BranchHues(tree, tree.RootNode);
        var folder = tree.ChildrenOf(tree.RootNode)[0];
        var arc = hues.Of(folder);

        Assert.All(tree.ChildrenOf(folder).ToArray(), child =>
            Assert.InRange(hues.Of(child).Centre, arc.From, arc.From + arc.Sweep));
    }

    /// <summary>
    /// The conversion checked against published values: sRGB red, white and black, and a mid grey,
    /// in Ottosson's own figures for the space.
    /// </summary>
    [Theory]
    [InlineData(0.627955, 0.257683, 29.2339, 255, 0, 0)]
    [InlineData(1.0, 0.0, 0.0, 255, 255, 255)]
    [InlineData(0.0, 0.0, 0.0, 0, 0, 0)]
    [InlineData(0.866440, 0.294827, 142.4953, 0, 255, 0)]
    public void OklchConvertsToTheSrgbColourItNames(double l, double c, double h, int red, int green, int blue)
    {
        var colour = Oklch.ToSrgb(l, c, h);

        Assert.InRange(colour.Red, red - 1, red + 1);
        Assert.InRange(colour.Green, green - 1, green + 1);
        Assert.InRange(colour.Blue, blue - 1, blue + 1);
    }

    /// <summary>
    /// A colour sRGB cannot show keeps its lightness and hue and loses chroma. Clipping instead
    /// shifts the hue towards a primary, so two neighbouring branches could come out the same.
    /// </summary>
    [Fact]
    public void AColourOutsideTheGamutKeepsItsHueAndLosesChroma()
    {
        // Far more chroma than any sRGB colour has at this lightness.
        var mapped = Oklch.ToSrgb(0.7, 0.5, 250);
        var inside = Oklch.ToSrgb(0.7, 0.05, 250);

        // Blue-dominant at a blue hue, as the low-chroma version of the same colour is.
        Assert.True(mapped.Blue > mapped.Red && mapped.Blue > mapped.Green);
        Assert.True(inside.Blue > inside.Red);
    }

    private static IEnumerable<int> Below(ExploreTree tree, int node)
    {
        foreach (var child in tree.ChildrenOf(node).ToArray())
        {
            yield return child;

            foreach (var deeper in Below(tree, child))
            {
                yield return deeper;
            }
        }
    }

    /// <summary>Four folders under the root, each with two folders of three files.</summary>
    private static ExploreTree Nested()
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        var top = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. Enumerable.Range(0, 4).Select(i => new ExploreChild($"top{i}", IsDirectory: true, IsLink: false, Size: 0))]);

        for (var i = 0; i < 4; i++)
        {
            var middle = builder.AddChildren(
                top + i,
                [.. Enumerable.Range(0, 2).Select(j => new ExploreChild($"mid{i}{j}", IsDirectory: true, IsLink: false, Size: 0))]);

            for (var j = 0; j < 2; j++)
            {
                builder.AddChildren(
                    middle + j,
                    [.. Enumerable.Range(0, 3).Select(k =>
                        new ExploreChild($"f{i}{j}{k}", IsDirectory: false, IsLink: false, Size: (4 - i) * 100 + k))]);
            }
        }

        return builder.Build(ExploreChildOrder.BySize);
    }
}
