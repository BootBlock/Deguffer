namespace Deguffer.Core.Viewing;

/// <summary>
/// Where the window is, and the pages a link led the reader away from, so Back returns to where each
/// link was. Only the window's own moves are remembered: a page the reader chose for themselves is
/// where they meant to be, and Back from there would be a way back to somewhere they already left by
/// choice.
///
/// <para>So a choice of their own forgets the trail, rather than going on top of it. Back then never
/// leads somewhere the reader did not see a link take them from, and the rail stays the one way
/// between pages it always was.</para>
///
/// <para>Every move, the reader's and Back's included, sets where the window is, because that is
/// where the next link is followed from.</para>
///
/// <para>Generic over the place, because the pages are the shell's and the rule is not (G1).</para>
/// </summary>
/// <typeparam name="T">What a place is: one of the window's destinations.</typeparam>
/// <param name="start">Where the window opens.</param>
public sealed class LinkTrail<T>(T start)
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

    /// <summary>Where the window is.</summary>
    public T Here { get; private set; } = start;

    /// <summary>Whether <see cref="Back"/> leads anywhere.</summary>
    public bool CanGoBack => _back.Count > 0;

    /// <summary>
    /// A link takes the reader from <see cref="Here"/> to <paramref name="to"/>. A link to the page
    /// they are on moves nothing, so there is nowhere for Back to return to and nothing is kept.
    /// </summary>
    public void Follow(T to)
    {
        var from = Here;
        Here = to;

        if (EqualityComparer<T>.Default.Equals(from, to))
        {
            return;
        }

        _back.Add(from);

        if (_back.Count > Depth)
        {
            _back.RemoveAt(0);
        }
    }

    /// <summary>
    /// Where the nearest link was followed from, which is now where the window is and is forgotten
    /// from the trail, or null where no link was followed. Nothing changes on null.
    /// </summary>
    public T? Back()
    {
        if (_back.Count == 0)
        {
            return null;
        }

        var place = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        Here = place;
        return place;
    }

    /// <summary>The reader chose <paramref name="to"/> for themselves: see the remarks on the type.</summary>
    public void Choose(T to)
    {
        Here = to;
        _back.Clear();
    }
}
