namespace Deguffer.Core.Configuration;

/// <summary>
/// How the Explore page draws what it found.
///
/// <para><see cref="Treemap"/> is the shipped view: it is what every tool in this category shows by
/// default, so it is the one a user arrives already able to read. The other three are not
/// decoration beside it. <see cref="List"/> is the densest answer to "what is biggest", which is
/// the question most people actually open the page with. <see cref="Icicle"/> keeps area exactly
/// proportional at every level, has room to label several levels at once, and is the one picture
/// that stays still while a scan is filling it in: the treemap and the sunburst rearrange themselves
/// as a scan finds more. <see cref="Sunburst"/> is what the rest of this category
/// ships and the easiest of the four to point at, at the cost of exaggerating whatever is deepest —
/// an outer ring gives more area for the same proportion, because area grows with the square of the
/// radius.</para>
///
/// <para><see cref="Tree"/> is the two together: a list that opens where the pictures nest. It is
/// what a list cannot do — show what is inside something without leaving what holds it — and what a
/// picture cannot do — be read a row at a time. The Memory page offers it, because a process tree
/// goes several levels deep and a reader following one down a flat list loses the rest of the
/// machine at the first step.</para>
///
/// <para><see cref="Files"/> is the one that ignores the folders: the largest files at any depth
/// under the node on screen, filtered by name, type, size and age. An ISO in a forgotten folder is
/// hard to find by drilling into a map and is the first row here. Explore offers it, and Memory has
/// no files to list.</para>
///
/// <para>The values are ordinal, and a view picker lists them in this order. A destination offers
/// the views that suit what it draws rather than all of them. They are stored by name rather than
/// by number, so the order is a presentation decision and not a compatibility one.</para>
/// </summary>
public enum ExploreView
{
    Treemap = 0,
    Icicle = 1,
    Sunburst = 2,
    List = 3,
    Tree = 4,
    Files = 5,
}
