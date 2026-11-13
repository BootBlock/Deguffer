using System.Collections;
using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>A drawing's rectangles that note which of them are read, and how many reads there were, by index or by enumeration.</summary>
internal sealed class WatchedTiles(IReadOnlyList<ExploreTile> tiles) : IReadOnlyList<ExploreTile>
{
    public HashSet<int> Read { get; } = [];

    /// <summary>How many times a rectangle was read, each read of one counted again.</summary>
    public int Reads { get; set; }

    public int Count => tiles.Count;

    public ExploreTile this[int index]
    {
        get
        {
            Read.Add(index);
            Reads++;
            return tiles[index];
        }
    }

    public IEnumerator<ExploreTile> GetEnumerator()
    {
        for (var i = 0; i < tiles.Count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
