namespace Deguffer.Core.Startup;

/// <summary>
/// What the Startup page orders its entries by (§7.5). <see cref="Name"/> is the shipped order.
/// Nothing orders by the memory a program uses: the page never ranks an entry by what it costs (§2).
///
/// <para>The values are ordinal, and the page's order picker lists them in this order. They are
/// stored by name rather than by number.</para>
/// </summary>
public enum StartupOrder
{
    /// <summary>By the title the row shows, A to Z.</summary>
    Name = 0,

    /// <summary>Whether it starts: the entries that start, then the disabled ones, then the unknown.</summary>
    State = 1,

    /// <summary>By the place it comes from, in the order the page reads them.</summary>
    Source = 2,

    /// <summary>By who made the program, A to Z, with every entry whose publisher is not known last.</summary>
    Publisher = 3,
}

/// <summary>Which way round the Startup page's chosen order runs. Stored by name.</summary>
public enum StartupOrderDirection
{
    Ascending = 0,

    Descending = 1,
}

/// <summary>
/// How the Startup page compares two entries under the order the user chose (§7.5).
///
/// <para><b>A tie goes by name, A to Z, whichever way round the order runs</b>, and then by source, as
/// the reading itself does. Reversing an order reverses what was chosen, not the alphabet inside each
/// group, so the disabled entries read in the same order whether they stand first or last.</para>
///
/// <para><b>An entry with no known publisher stands last either way round.</b> Descending would
/// otherwise open the list with every entry Deguffer could not name a maker for, which answers
/// nothing the reader asked.</para>
/// </summary>
public static class StartupRowOrder
{
    // Current culture and ignoring case: these are names a reader reads, in the reader's own language.
    private static readonly StringComparer Words = StringComparer.CurrentCultureIgnoreCase;

    /// <summary>
    /// <paramref name="order"/> if a member names it, and otherwise the shipped order. The preferences
    /// file is read with numbers allowed for an enum, so a hand-edited <c>"StartupOrder": 9</c>
    /// arrives as an order nothing can sort by or show.
    /// </summary>
    public static StartupOrder Known(StartupOrder order) =>
        Enum.IsDefined(order) ? order : StartupOrder.Name;

    /// <summary><paramref name="direction"/> if a member names it, and otherwise ascending, for the reason <see cref="Known(StartupOrder)"/> gives.</summary>
    public static StartupOrderDirection Known(StartupOrderDirection direction) =>
        Enum.IsDefined(direction) ? direction : StartupOrderDirection.Ascending;

    /// <summary>How <paramref name="order"/>, run <paramref name="direction"/>, compares two entries: negative when the first goes above the second.</summary>
    public static IComparer<StartupEntry> Comparer(StartupOrder order, StartupOrderDirection direction) =>
        Known(direction) is StartupOrderDirection.Descending ? Descending[(int)Known(order)] : Ascending[(int)Known(order)];

    private static readonly IComparer<StartupEntry>[] Ascending = [.. Enum.GetValues<StartupOrder>().Select(order => Build(order, 1))];

    private static readonly IComparer<StartupEntry>[] Descending = [.. Enum.GetValues<StartupOrder>().Select(order => Build(order, -1))];

    private static IComparer<StartupEntry> Build(StartupOrder order, int sign) =>
        order switch
        {
            StartupOrder.State => Comparer<StartupEntry>.Create((a, b) => Then(sign * Rank(a.State).CompareTo(Rank(b.State)), a, b)),
            StartupOrder.Source => Comparer<StartupEntry>.Create((a, b) => Then(sign * a.Key.Source.CompareTo(b.Key.Source), a, b)),
            StartupOrder.Publisher => Comparer<StartupEntry>.Create((a, b) => Then(ByPublisher(a.Publisher, b.Publisher, sign), a, b)),
            _ => Comparer<StartupEntry>.Create((a, b) => sign * Words.Compare(a.Title, b.Title) is var byTitle and not 0
                ? byTitle
                : a.Key.Source.CompareTo(b.Key.Source)),
        };

    /// <summary>The entries that start first, because that is the question the page opens on.</summary>
    private static int Rank(StartupState state) => state switch
    {
        StartupState.Starts => 0,
        StartupState.Disabled => 1,
        _ => 2,
    };

    private static int ByPublisher(string? a, string? b, int sign) => (a, b) switch
    {
        (null, null) => 0,
        (null, _) => 1,
        (_, null) => -1,
        _ => sign * Words.Compare(a, b),
    };

    private static int Then(int compared, StartupEntry a, StartupEntry b) =>
        compared != 0 ? compared
            : Words.Compare(a.Title, b.Title) is var byTitle and not 0 ? byTitle
            : a.Key.Source.CompareTo(b.Key.Source);
}
