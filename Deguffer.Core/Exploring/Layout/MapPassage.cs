namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// The two legs of a change of folder between folders where neither holds the other
/// (<see cref="FolderStep.Through"/>): out of the first folder's picture into the picture of the
/// folder that holds both, then on into the second's, as one flight on one clock.
///
/// <para>Each leg is a <see cref="MapDescent"/> about the middle picture: <see cref="Out"/> has the
/// folder left inside it, and <see cref="In"/> the folder going to. At <see cref="Midway"/> the middle
/// picture fills the screen, which is where the one leg ends and the other begins, so nothing jumps
/// between them.</para>
///
/// <para>The time is shared out by how far each leg travels (<see cref="MapDescent.Depth"/>), so the
/// camera keeps the same speed through the turn: pulling back out of a small folder and flying into a
/// large one spends most of the flight on the way out. Where a leg does not fly, with motion off or
/// for a folder with no shape on the middle picture, the legs share it equally: that leg fades in
/// place rather than being cut, and there is no speed to keep through the turn.</para>
///
/// <para>The clock is the caller's: this says which leg a progress from 0, the first folder filling
/// the screen, to 1, the second filling it, is on, and how far along that leg.</para>
/// </summary>
public readonly record struct MapPassage(MapDescent Out, MapDescent In)
{
    /// <summary>The progress at which the middle picture fills the screen: the end of <see cref="Out"/> and the start of <see cref="In"/>.</summary>
    public double Midway
    {
        get
        {
            var whole = Out.Depth + In.Depth;

            return Out.Flies && In.Flies && whole > 0 ? Out.Depth / whole : 0.5;
        }
    }

    /// <summary>
    /// Which leg <paramref name="progress"/> is on, and how far along that leg in the leg's own terms
    /// (see <see cref="MapDescent"/>): 1 to 0 on the way out, with the folder left filling the screen
    /// at 1, and 0 to 1 on the way in.
    /// </summary>
    public (MapDescent Leg, double Progress) At(double progress)
    {
        progress = Math.Clamp(progress, 0, 1);

        var midway = Midway;

        return progress < midway
            ? (Out, 1 - (progress / midway))
            : (In, midway < 1 ? (progress - midway) / (1 - midway) : 1);
    }
}
