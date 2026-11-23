namespace Deguffer.Core.Configuration;

/// <summary>
/// What the colours on the Explore map say.
///
/// <para>Orthogonal to <see cref="ExploreView"/> rather than a fifth entry in it, because it is a
/// different question: the view decides where the shapes go, and this decides what painting one of
/// them a colour tells the reader. Every combination of the two is meaningful, and folding them
/// into one list would offer six choices where there are two.</para>
///
/// <para><see cref="Branch"/> is the shipped one and answers "what is this part of", which is the
/// question a map of a drive is opened with. <see cref="Age"/> answers §8's first open question
/// instead — whether a toolchain is idle — which a size picture cannot show at all: an Android SDK
/// and a working project look identical by size, and the whole difference is that nothing has
/// written to one of them in two years. <see cref="Growth"/> answers "what filled the drive since I last
/// looked", which a size picture shows only once the culprit is the largest thing on the disk.
/// <see cref="Type"/> answers "what kind of thing is this space", which is often the quickest way to
/// know what it is: a folder of video and a folder of disk images look alike by size.
/// <see cref="Depth"/> answers "how deep is this", the question the earliest disk maps coloured
/// by: a whole level of the tree in one colour, the next in another.</para>
/// </summary>
public enum ExploreColouring
{
    /// <summary>
    /// A hue per folder, inside its parent's part of the hue circle, lighter by depth. See
    /// <c>TilePalette</c> and <c>BranchHues</c>.
    /// </summary>
    Branch = 0,

    /// <summary>
    /// A band per age, by the newest write anywhere at or below the shape. See <c>AgePalette</c>,
    /// and <c>ExploreTree.ModifiedOf</c> for why it is the newest write below rather than the
    /// shape's own date.
    /// </summary>
    Age = 1,

    /// <summary>
    /// A band per change in bytes since the last scan of the same volume, through grey for no change.
    /// See <c>GrowthPalette</c> and <c>ScanGrowth</c>.
    /// </summary>
    Growth = 2,

    /// <summary>
    /// A colour per kind of file, by the name: a file's own kind, and a folder's largest kind by bytes.
    /// See <c>TypePalette</c> and <c>DominantTypes</c>.
    /// </summary>
    Type = 3,

    /// <summary>
    /// A colour per level below the folder on screen, the same for every shape at that level and
    /// repeating every eight levels. See <c>DepthPalette</c>.
    /// </summary>
    Depth = 4,
}
