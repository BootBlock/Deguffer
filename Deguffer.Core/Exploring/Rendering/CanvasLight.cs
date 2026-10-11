namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// The pointer's light on one canvas shaded on the GPU, and when to shade it: once the compositor has
/// taken the surface as it is, and then only where the light was last wanted.
///
/// <para>Never in the commit that shaded the canvas whole. A part of a virtual surface written in the
/// same commit as the whole of it left the rest of the surface empty on screen, which a light landing
/// with a new drawing did every time. And a pointer moves several times a frame, so the light is
/// shaded once a commit rather than once a move, over one rectangle round where it was and where it
/// is (<see cref="PointerLight.Between"/>).</para>
///
/// <para>The owner asks the compositor for each commit this numbers, and hands its number back to
/// <see cref="Committed"/> once it is done. Only the last asked for settles the canvas, so a canvas
/// shaded whole after a light was asked for waits for its own commit.</para>
/// </summary>
public sealed class CanvasLight
{
    private (CushionQuads Quads, TileColour Ground)? _shaded;

    private PointerLight? _light;

    private PointerLight? _wanted;

    private int _asked;

    private bool _awaiting;

    /// <summary>
    /// The whole canvas was shaded from <paramref name="quads"/> on <paramref name="ground"/>, with no
    /// light on it. Returns the commit to ask for, after which a light wanted on it is shaded.
    /// </summary>
    public int Shaded(CushionQuads quads, TileColour ground)
    {
        ArgumentNullException.ThrowIfNull(quads);

        _shaded = (quads, ground);
        _light = null;

        return Ask();
    }

    /// <summary>The surface no longer holds a canvas shaded on the GPU, or a light on one.</summary>
    public void Forget()
    {
        _shaded = null;
        _light = null;
        _wanted = null;
    }

    /// <summary>
    /// Want the light at <paramref name="light"/>, or no light with null. Returns the commit to ask for,
    /// or null where one is already asked for, the light is already there, or there is no canvas
    /// shaded on the GPU to light.
    /// </summary>
    public int? Want(PointerLight? light)
    {
        if (_shaded is null)
        {
            return null;
        }

        _wanted = light;

        return !_awaiting && _wanted != _light ? Ask() : null;
    }

    /// <summary>
    /// Commit <paramref name="commit"/> is done. Returns what to shade now: the region, from the quads
    /// on the ground, under the light, which is null to take it off. Null where that commit was
    /// superseded, or nothing is to be shaded.
    /// </summary>
    public CanvasLightShading? Committed(int commit)
    {
        if (commit != _asked)
        {
            return null;
        }

        _awaiting = false;

        if (_shaded is not { } shaded || _wanted == _light)
        {
            return null;
        }

        var region = PointerLight.Between(_light, _wanted, shaded.Quads.Width, shaded.Quads.Height);
        _light = _wanted;

        return region is { } changed ? new CanvasLightShading(shaded.Quads, shaded.Ground, changed, _light) : null;
    }

    private int Ask()
    {
        _awaiting = true;

        return ++_asked;
    }
}
