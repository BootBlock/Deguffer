namespace Deguffer.Core.Viewing;

/// <summary>Whether anyone can see a part of one of Deguffer's windows.</summary>
public enum Sight
{
    /// <summary>Not on screen at all: its page has gone, or its window is minimised or hidden.</summary>
    Unseen,

    /// <summary>On screen as far as the XAML host knows, and hidden all the same (<see cref="WindowCover"/>).</summary>
    Covered,

    /// <summary>Where the reader can see it.</summary>
    Seen,
}

/// <summary>
/// Which <see cref="Sight"/> a part of a window is in: the one decision behind everything Deguffer stops
/// doing for nobody, a starfield's flight and a page's readings alike.
///
/// <para><b>Every doubt answers "seen".</b> Work that goes on behind a window costs processor time, where
/// work stopped in front of the reader is a fault they can see.</para>
/// </summary>
public static class Sights
{
    /// <param name="loaded">Whether the part is in the window's tree.</param>
    /// <param name="hostVisible">
    /// Whether the XAML host says the window is shown, which it is not minimised or hidden, or null where
    /// the part is in no host that can say.
    /// </param>
    /// <param name="covered">
    /// Whether other windows, the screens' edges or a virtual desktop hide every pixel of the part, which
    /// Windows never tells the host. Asked only of a part that is otherwise shown, because answering it
    /// reads every window in front.
    /// </param>
    public static Sight Of(bool loaded, bool? hostVisible, Func<bool> covered)
    {
        ArgumentNullException.ThrowIfNull(covered);

        if (!loaded || hostVisible is false)
        {
            return Sight.Unseen;
        }

        return covered() ? Sight.Covered : Sight.Seen;
    }
}
