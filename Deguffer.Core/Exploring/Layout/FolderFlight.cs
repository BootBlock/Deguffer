using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// Where a change of folder on the map is, from the moment the map is handed another folder until the
/// flight has finished and the picture it went to has arrived: one of a closed set of phases, each
/// holding what it needs, and each move to the next a member that gives it.
///
/// <para>The map holds one of these and replaces it at each move, so it can never be in two phases
/// at once, such as waiting for the picture it goes out to while it waits for the picture it went
/// into. The flight's clock, its drawings and the screen are the map's: this says only which phase
/// the change is in.</para>
/// </summary>
public abstract record FolderFlight
{
    private FolderFlight(FolderLeft? left, FolderPassed? passing) => (Left, Passing) = (left, passing);

    /// <summary>No change of folder on its way: <see cref="FolderStep.Across"/>.</summary>
    public static FolderFlight None { get; } = new Settled();

    /// <summary>The folder the change left, as it was on screen, or null where there is no change.</summary>
    public FolderLeft? Left { get; }

    /// <summary>
    /// The folder a change of folder between folders where neither holds the other passes through, on
    /// its way out to it, and null otherwise and from the middle of that flight on. The picture drawn
    /// meanwhile is of this folder rather than of the folder asked for. See <see cref="FolderStep.Through"/>.
    /// </summary>
    public FolderPassed? Passing { get; }

    /// <summary>Whether a change of folder is on its way, flying or waiting for a picture.</summary>
    public bool IsUnderWay => this is not Settled;

    /// <summary>
    /// Whether the picture left is the inner one: on a step out, which it shrinks back into, and on the
    /// way out of a flight through a folder. Otherwise the picture going to is, which grows out of the
    /// picture left. The inner picture is drawn over the outer one.
    /// </summary>
    public bool Outward => this is WaitingToLeave or LeavingOut or WaitingToPass or PassingOut;

    /// <summary>
    /// Whether a step out is waiting for the picture it goes out to, or a flight through a folder for
    /// the picture of that folder: until it is drawn there is nothing to pull back into, and the
    /// picture left stays, filling the screen.
    /// </summary>
    public bool AwaitsOuter => this is WaitingToLeave or WaitingToPass;

    /// <summary>
    /// Whether a flight into a folder has finished before the picture of it arrived. The picture left
    /// stays, stretched where the flight left it, until the new one has landed over it.
    /// </summary>
    public bool Owed => this is Landing;

    /// <summary>
    /// The change of folder starting from a settled map, by <paramref name="step"/>, leaving
    /// <paramref name="left"/>, for a folder opened at <paramref name="opened"/>. A step in flies at once,
    /// into a picture still to be drawn. A step out waits for the picture it goes out to, and a flight
    /// through a folder for the picture of that folder, which is drawn in the meantime.
    /// </summary>
    /// <exception cref="ArgumentException">For a step that does not fly, or a flight through a folder that names none.</exception>
    public static FolderFlight Begin(PictureStep step, FolderLeft left, MapViewport opened)
    {
        ArgumentNullException.ThrowIfNull(left);

        return step switch
        {
            { Way: FolderStep.Into } => new FlyingIn(left),
            { Way: FolderStep.OutOf } => new WaitingToLeave(left),
            { Way: FolderStep.Through, Meeting: { } meeting } => new WaitingToPass(left, new FolderPassed(meeting, opened)),
            _ => throw new ArgumentException($"A change of folder {step} has no flight.", nameof(step)),
        };
    }

    /// <summary>
    /// The picture this was waiting for has been drawn (see <see cref="AwaitsOuter"/>): a flight through
    /// a folder flies out to it, whether or not the folder left has a shape in it, because a folder
    /// with none fades on its leg (<see cref="MapPassage"/>). A step out flies only from a shape of the
    /// folder left, where <paramref name="leftDrawn"/>, and otherwise has nothing to fly from and ends.
    /// </summary>
    /// <exception cref="InvalidOperationException">Where nothing was waiting.</exception>
    public FolderFlight OuterDrawn(bool leftDrawn) => this switch
    {
        WaitingToPass waiting => new PassingOut(waiting.Left!, waiting.Passing!),
        WaitingToLeave waiting => leftDrawn ? new LeavingOut(waiting.Left!) : None,
        _ => throw new InvalidOperationException($"A change of folder {GetType().Name} waits for no picture."),
    };

    /// <summary>
    /// A flight through a folder has reached the middle picture, of the folder passed through, which
    /// fills the screen at <paramref name="passingAt"/>: from here it is a step into the folder asked
    /// for, out of that picture. The folder left is now the folder passed through, drawn
    /// <paramref name="drawing"/> in <paramref name="colours"/>, of <paramref name="shown"/>'s tree in its view.
    /// </summary>
    /// <exception cref="InvalidOperationException">For any other phase, which has no middle.</exception>
    public FolderFlight Midway(MapPicture shown, MapViewport passingAt, ExploreSurface? drawing, ShapeColours? colours) =>
        this is PassingOut
            ? new FlyingIn(new FolderLeft(Drawn(shown), passingAt, drawing, colours))
            : throw new InvalidOperationException($"A change of folder {GetType().Name} has no middle.");

    /// <summary>
    /// The flight has arrived. The change ends, unless the picture it went to is still landing, where
    /// <paramref name="landing"/>: then the picture left stays under it until it arrives (see <see cref="Owed"/>).
    /// </summary>
    public FolderFlight Arrived(bool landing) => landing && Left is { } left ? new Landing(left) : None;

    /// <summary>
    /// The reader asked for the folder this is leaving before it arrived: the change goes back the
    /// other way, and leaves <paramref name="leaving"/>, the folder it was going to as far as it got.
    /// It flies out where it was flying in, and in where it was on its way out, through a folder
    /// included, which it never reaches. A change that had not started to fly turns round where it is.
    /// </summary>
    /// <exception cref="InvalidOperationException">With no change on its way.</exception>
    public FolderFlight Turned(FolderLeft leaving)
    {
        ArgumentNullException.ThrowIfNull(leaving);

        return IsUnderWay
            ? Outward ? new FlyingIn(leaving) : new LeavingOut(leaving)
            : throw new InvalidOperationException("No change of folder is on its way to turn round.");
    }

    /// <summary>
    /// The picture drawn while the map was handed <paramref name="shown"/>: the folder passed through on
    /// the way out of a flight through a folder, drawn opened and with no volume beside it as any folder
    /// the reader opens is, and <paramref name="shown"/> itself otherwise.
    /// </summary>
    public MapPicture Drawn(MapPicture shown) =>
        Passing is { } passing ? shown with { Node = passing.Folder, Volume = VolumeSpace.None } : shown;

    /// <summary>No change of folder on its way.</summary>
    private sealed record Settled : FolderFlight
    {
        public Settled()
            : base(null, null)
        {
        }
    }

    /// <summary>Flying into a folder, out of the picture left, as the picture of it is drawn and arrives into the flight.</summary>
    private sealed record FlyingIn : FolderFlight
    {
        public FlyingIn(FolderLeft left)
            : base(left, null)
        {
        }
    }

    /// <summary>Flown into a folder, with its picture still landing.</summary>
    private sealed record Landing : FolderFlight
    {
        public Landing(FolderLeft left)
            : base(left, null)
        {
        }
    }

    /// <summary>A step out, waiting for the picture it goes out to.</summary>
    private sealed record WaitingToLeave : FolderFlight
    {
        public WaitingToLeave(FolderLeft left)
            : base(left, null)
        {
        }
    }

    /// <summary>Flying out of the folder left, which shrinks back into its shape in the picture it went out to.</summary>
    private sealed record LeavingOut : FolderFlight
    {
        public LeavingOut(FolderLeft left)
            : base(left, null)
        {
        }
    }

    /// <summary>A flight through a folder, waiting for the picture of the folder it passes through.</summary>
    private sealed record WaitingToPass : FolderFlight
    {
        public WaitingToPass(FolderLeft left, FolderPassed passing)
            : base(left, passing)
        {
        }
    }

    /// <summary>A flight through a folder on its way out to the folder it passes through, before the middle.</summary>
    private sealed record PassingOut : FolderFlight
    {
        public PassingOut(FolderLeft left, FolderPassed passing)
            : base(left, passing)
        {
        }
    }
}
