namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// The way a change of folder on the map flies, and which part of it is under way: one
/// <see cref="MapDescent"/> into a folder or out of it, or a <see cref="MapPassage"/> out of one folder
/// and on into another through the folder that holds both, on its way out, turned back on its way out,
/// or on its way in.
///
/// <para>A value: whatever moves the flight on gives another. The clock is the caller's, and a
/// progress runs from 0 to 1 as <see cref="MapDescent"/> and <see cref="MapPassage"/> say.</para>
/// </summary>
public readonly record struct MapCourse
{
    private readonly MapDescent _path;

    private readonly MapPassage? _passage;

    private readonly Leg _leg;

    private MapCourse(MapDescent path, MapPassage? passage, Leg leg) =>
        (_path, _passage, _leg) = (path, passage, leg);

    /// <summary>Which part of a flight through a folder is under way.</summary>
    private enum Leg
    {
        /// <summary>On the way out, going on into the folder asked for past the middle picture.</summary>
        Out,

        /// <summary>
        /// On the way out, turned round to go back to the folder it left: it never goes on past the
        /// middle picture into a folder no longer asked for.
        /// </summary>
        Back,

        /// <summary>Past the middle picture, on the way into the folder asked for.</summary>
        In,
    }

    /// <summary>A flight into a folder or out of it, along <paramref name="path"/>.</summary>
    public static MapCourse Descent(MapDescent path) => new(path, null, Leg.Out);

    /// <summary>A flight out of one folder and on into another, along <paramref name="passage"/>, starting on its way out.</summary>
    public static MapCourse Through(MapPassage passage) => new(default, passage, Leg.Out);

    /// <summary>
    /// The course once the flight is at <paramref name="progress"/>, and whether that is the moment a
    /// flight through a folder reaches the middle picture. Reported once, and never for one turned
    /// round on its way out, which goes back to the folder it left.
    /// </summary>
    public (MapCourse Course, bool Midway) Reached(double progress) =>
        _passage is { } passage && _leg == Leg.Out && progress >= passage.Midway
            ? (new MapCourse(_path, passage, Leg.In), true)
            : (this, false);

    /// <summary>
    /// The course turned round, and the progress it goes back to from a flight that was going to
    /// <paramref name="target"/>. A flight into a folder or out of it goes back to the other end. A
    /// flight through a folder turns between the ends of the leg under way: the folder it left and
    /// the middle picture, or, past the middle, the middle picture and the folder going to.
    /// </summary>
    public (MapCourse Course, double Back) Turned(double target)
    {
        if (_passage is not { } passage)
        {
            return (this, 1 - target);
        }

        var (start, end) = _leg == Leg.In ? (passage.Midway, 1.0) : (0.0, passage.Midway);

        return (new MapCourse(_path, passage, _leg == Leg.In ? Leg.In : Leg.Back), target == start ? end : start);
    }

    /// <summary>
    /// Which descent the flight at <paramref name="progress"/> is on, and how far along it in its own
    /// terms (see <see cref="MapDescent"/>).
    /// </summary>
    public (MapDescent Path, double Progress) At(double progress) => _passage switch
    {
        null => (_path, progress),

        // Turned round on the way out, the spring carries its speed on a little before it comes back,
        // and can carry it past the middle: the way out holds there rather than going on into a folder
        // no longer asked for.
        { } passage when _leg == Leg.Back => passage.OutAt(progress),

        { } passage => passage.At(progress),
    };
}
