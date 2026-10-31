using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which shapes the map marks out: a shape carries one outline, the strongest claim on it, and the
/// hatch over what is being removed goes only on a drawing of the tree it names (§7.1).
/// </summary>
public sealed class MapMarksTests
{
    [Fact]
    public void APickOfWhatIsPickedAlreadyChangesNothing()
    {
        var marks = new MapMarks();

        Assert.True(marks.Pick([3, 5]));
        Assert.False(marks.Pick([5, 3]));
        Assert.True(marks.Pick([5]));
        Assert.Equal([5], marks.Picked);
    }

    [Fact]
    public void ThePointerOutlinesANodeThatIsNotPicked()
    {
        var marks = new MapMarks();

        Assert.Equal([7], marks.Hover(new ExploreHit(7, 100)));
    }

    /// <summary>A picked shape is outlined as picked, so the pointer over it adds no weaker outline on top.</summary>
    [Fact]
    public void ThePointerOutlinesNothingThatIsPicked()
    {
        var marks = new MapMarks();
        marks.Pick([7]);

        Assert.Empty(marks.Hover(new ExploreHit(7, 100)));
    }

    /// <summary>A block standing for the rest of the volume cannot be picked, so it is not outlined as if it could.</summary>
    [Fact]
    public void ThePointerOutlinesNoBlock()
    {
        var marks = new MapMarks();

        Assert.Empty(marks.Hover(new ExploreHit(ExploreTile.FreeSpace, 5000)));
    }

    [Fact]
    public void ThePointerOverNothingOutlinesNothing()
    {
        var marks = new MapMarks();
        marks.Hover(new ExploreHit(7, 100));

        Assert.Empty(marks.Hover(null));
    }

    [Fact]
    public void TheKeyboardsShapeIsOutlinedWhereNothingElseMarksIt()
    {
        var marks = new MapMarks();
        marks.Hover(new ExploreHit(7, 100));

        Assert.True(marks.OutlinesFocus(9));
    }

    [Fact]
    public void TheKeyboardsShapeIsNotOutlinedAgainWherePicked()
    {
        var marks = new MapMarks();
        marks.Pick([9]);

        Assert.False(marks.OutlinesFocus(9));
    }

    [Fact]
    public void TheKeyboardsShapeIsNotOutlinedAgainUnderThePointer()
    {
        var marks = new MapMarks();
        marks.Hover(new ExploreHit(9, 100));

        Assert.False(marks.OutlinesFocus(9));
    }

    /// <summary>The pointer leaving the keyboard's shape gives the shape its own outline back.</summary>
    [Fact]
    public void TheKeyboardsShapeIsOutlinedOnceThePointerLeavesIt()
    {
        var marks = new MapMarks();
        marks.Hover(new ExploreHit(9, 100));
        marks.Hover(null);

        Assert.True(marks.OutlinesFocus(9));
    }

    [Fact]
    public void WhatIsBeingRemovedIsHatchedInTheTreeItNames()
    {
        var tree = Drawings.Tree(["file0", "file1"]);
        var marks = new MapMarks();

        marks.MarkRemoving(tree, [1, 2]);

        Assert.Equal([1, 2], marks.RemovingIn(tree).Order());
    }

    /// <summary>The same numbers in another tree, even a later tree of the same place, are other shapes (§7.1).</summary>
    [Fact]
    public void WhatIsBeingRemovedIsHatchedInNoOtherTree()
    {
        var tree = Drawings.Tree(["file0", "file1"]);
        var marks = new MapMarks();

        marks.MarkRemoving(tree, [1, 2]);

        Assert.Empty(marks.RemovingIn(Drawings.Tree(["file0", "file1"])));
    }

    [Fact]
    public void ARemovalOverHatchesNothing()
    {
        var tree = Drawings.Tree(["file0"]);
        var marks = new MapMarks();
        marks.MarkRemoving(tree, [1]);

        marks.MarkRemoving(null, []);

        Assert.Empty(marks.RemovingIn(tree));
    }
}
