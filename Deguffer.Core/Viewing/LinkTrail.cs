namespace Deguffer.Core.Viewing;

/// <summary>
/// The pages a link led the reader away from, so Back returns to where each link was. Only the
/// window's own moves are remembered: a page the reader chose for themselves is where they meant to
/// be, and Back from there would be a way back to somewhere they already left by choice.
///
/// <para>So a choice of their own forgets the trail, rather than going on top of it. Back then never
/// leads somewhere the reader did not see a link take them from, and the rail stays the one way
/// between pages it always was.</para>
///
/// <para>Generic over the place, because the pages are the shell's and the rule is not (G1).</para>
/// </summary>
/// <typeparam name="T">What a place is: one of the window's destinations.</typeparam>
public sealed class LinkTrail<T>
    where T : struct
{
    /// <summary>
    /// How many links are remembered, the oldest forgotten first. Bounded because two pages that link
    /// to each other can be followed back and forth without end, and nothing here is ever read past
    /// the nearest few.
    /// </summary>
    public const int Depth = 50;

    /// <summary>Where Back leads, the nearest last.</summary>
    private readonly List<T> _back = [];

    /// <summary>Whether <see cref="Back"/> leads anywhere.</summary>
    public bool CanGoBack => _back.Count > 0;

    /// <summary>
    /// A link took the reader from <paramref name="from"/> to <paramref name="to"/>. A link to the
    /// page they are on moves nothing, so there is nowhere for Back to return to and nothing is kept.
    /// </summary>
    /// <returns>Whether the trail changed.</returns>
    public bool Follow(T from, T to)
    {
        if (EqualityComparer<T>.Default.Equals(from, to))
        {
            return false;
        }

        _back.Add(from);

        if (_back.Count > Depth)
        {
            _back.RemoveAt(0);
        }

        return true;
    }

    /// <summary>Where the nearest link was followed from, now forgotten, or null where no link was followed.</summary>
    public T? Back()
    {
        if (_back.Count == 0)
        {
            return null;
        }

        var place = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        return place;
    }

    /// <summary>The reader chose a page for themselves: see the remarks on the type.</summary>
    /// <returns>Whether the trail changed.</returns>
    public bool Forget()
    {
        if (_back.Count == 0)
        {
            return false;
        }

        _back.Clear();
        return true;
    }
}
