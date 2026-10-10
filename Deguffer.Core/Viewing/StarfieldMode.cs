namespace Deguffer.Core.Viewing;

/// <summary>What the About page's starfield is doing.</summary>
public enum StarfieldMode
{
    /// <summary>Not drawn at all.</summary>
    Absent,

    /// <summary>Drawn once, at rest, and never moved.</summary>
    Still,

    /// <summary>Stopped where it was, because nobody can see it, and carried on from there when they can.</summary>
    Paused,

    /// <summary>The camera flying through it.</summary>
    Flying,
}

/// <summary>Which <see cref="StarfieldMode"/> the About page's starfield is in.</summary>
public static class StarfieldModes
{
    /// <summary>
    /// The mode for a reader whose starfield plays as <paramref name="played"/>
    /// (<see cref="MotionToken.Starfield"/>).
    /// </summary>
    /// <param name="played">How the starfield plays with the reader's animation setting.</param>
    /// <param name="highContrast">
    /// Whether Windows is in a high contrast theme, which has no starfield: a theme the reader chose so
    /// that everything on the screen stands out has nothing on it that is only decoration.
    /// </param>
    /// <param name="effectsFast">
    /// Whether the compositor can draw effects without cost, which it cannot over Remote Desktop, where
    /// every frame of a moving field would go down the wire.
    /// </param>
    /// <param name="seen">Whether the page is on screen in a window that is not minimised or hidden.</param>
    public static StarfieldMode For(Motion played, bool highContrast, bool effectsFast, bool seen)
    {
        if (highContrast)
        {
            return StarfieldMode.Absent;
        }

        // A field that cannot move for this reader stands still whether or not it is seen, so there is
        // nothing to pause.
        if (!played.Travels || !effectsFast)
        {
            return StarfieldMode.Still;
        }

        return seen ? StarfieldMode.Flying : StarfieldMode.Paused;
    }
}
