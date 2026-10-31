using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.Files;
using Deguffer.Core.Exploring.History;
using Deguffer.Core.Exploring.Knowledge;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Exploring;

/// <summary>
/// What the Explore page knows, beside its tree, that the line under its map says
/// (<see cref="ExploreReadout"/>). Read from the page each time the pointer or the keyboard moves to
/// another shape, so the line always says what the screen shows.
/// </summary>
/// <param name="Now">The instant an age is told against: the page's clock, not the machine's.</param>
/// <param name="Colouring">How the map is coloured, which decides what the figures add to the size and age.</param>
/// <param name="Growth">The comparison with an earlier scan, where there is one.</param>
/// <param name="Dominant">Each folder's largest kind of file, where it is measured yet.</param>
/// <param name="Current">The node on screen, from which the depth colouring counts its levels.</param>
/// <param name="Guide">What the reference says of a place, for the note.</param>
/// <param name="Volume">The volume scanned, with what Windows states of the space it keeps.</param>
/// <param name="IsElevated">Whether this process runs as administrator, which decides why space went uncounted.</param>
/// <param name="Strategy">How the scan read the drive, which decides the same.</param>
public readonly record struct ExploreReadoutState(
    DateTime Now,
    ExploreColouring Colouring,
    ScanGrowth? Growth,
    DominantTypes? Dominant,
    int Current,
    ItemGuide Guide,
    VolumeSpace Volume,
    bool IsElevated,
    ScanStrategy Strategy);
