using System.Collections;
using Deguffer.App.Controls;

namespace Deguffer.App.Tests;

/// <summary>
/// How a step through the map's tree reaches a shape's sibling or child (<see cref="MapTreeSteps"/>): a
/// walk through a folder of tens of thousands of shapes reads each shape a bounded number of times, so a
/// search of the window over such a folder ends rather than costing the square of its size.
/// </summary>
public sealed class MapTreeStepsTests
{
    private const int Count = 50_000;

    /// <summary>A walk forward from the first shape visits every shape in order, then what follows them, reading no more than twice as many shapes as there are.</summary>
    [Fact]
    public void AWalkForwardReadsEachShapeABoundedNumberOfTimes()
    {
        var shapes = new CountedList(Count);
        var after = new Shape(-1);
        var asked = 0;
        var visited = new List<Shape>();

        for (Shape? at = shapes.Peek(0); at is not null; at = Next(at))
        {
            visited.Add(at);
        }

        Assert.Equal(Count + 1, visited.Count);
        Assert.Equal(Enumerable.Range(0, Count), visited.Take(Count).Select(shape => shape.Index));
        Assert.Same(after, visited[^1]);
        Assert.Equal(1, asked);
        Assert.InRange(shapes.Reads, Count, 2 * Count);

        Shape? Next(Shape at) => at == after ? null : MapTreeSteps.Sibling(shapes, at.Index, at, forward: true, () =>
        {
            asked++;
            return after;
        });
    }

    /// <summary>A walk back from the last shape visits every shape in reverse, stops at the first, reading no more than twice as many shapes as there are.</summary>
    [Fact]
    public void AWalkBackReadsEachShapeABoundedNumberOfTimes()
    {
        var shapes = new CountedList(Count);
        var visited = 0;

        for (Shape? at = shapes.Peek(Count - 1); at is not null; at = MapTreeSteps.Sibling(shapes, at.Index, at, forward: false, Unasked))
        {
            Assert.Equal(Count - 1 - visited, at.Index);
            visited++;
        }

        Assert.Equal(Count, visited);
        Assert.InRange(shapes.Reads, Count, 2 * Count);
    }

    /// <summary>
    /// A peer that is not at its place in its folder's shapes, as one kept from a folder collapsed since or
    /// from another drawing is not, has no siblings, whichever way it steps.
    /// </summary>
    [Fact]
    public void AShapeNotAtItsPlaceHasNoSiblings()
    {
        var shapes = new CountedList(3);
        var stranger = new Shape(1);

        Assert.Null(MapTreeSteps.Sibling(shapes, 1, stranger, forward: true, Unasked));
        Assert.Null(MapTreeSteps.Sibling(shapes, 1, stranger, forward: false, Unasked));
        Assert.Null(MapTreeSteps.Sibling(new CountedList(0), 1, stranger, forward: true, Unasked));
        Assert.Null(MapTreeSteps.Sibling(shapes, 3, stranger, forward: false, Unasked));
    }

    /// <summary>A folder's first and last children are read by their places, and a folder with none has neither.</summary>
    [Fact]
    public void TheFirstAndLastChildAreReadByTheirPlaces()
    {
        var shapes = new CountedList(Count);

        Assert.Equal(0, MapTreeSteps.Child(shapes, first: true)?.Index);
        Assert.Equal(Count - 1, MapTreeSteps.Child(shapes, first: false)?.Index);
        Assert.Equal(2, shapes.Reads);
        Assert.Null(MapTreeSteps.Child(new CountedList(0), first: true));
        Assert.Null(MapTreeSteps.Child(new CountedList(0), first: false));
    }

    private static Shape? Unasked() => throw new InvalidOperationException("What follows the shapes was asked for on a step that does not pass them.");

    private sealed record Shape(int Index);

    /// <summary>A folder's shapes that counts each one read, and refuses to be enumerated, which reads them all.</summary>
    private sealed class CountedList(int count) : IReadOnlyList<Shape>
    {
        private readonly Shape[] _shapes = [.. Enumerable.Range(0, count).Select(index => new Shape(index))];

        public int Reads { get; private set; }

        public int Count => _shapes.Length;

        public Shape this[int index]
        {
            get
            {
                Reads++;
                return _shapes[index];
            }
        }

        /// <summary>The shape at <paramref name="index"/>, or null past the end, without counting a read, for where a walk starts.</summary>
        public Shape? Peek(int index) => index < _shapes.Length ? _shapes[index] : null;

        public IEnumerator<Shape> GetEnumerator() => throw new InvalidOperationException("A step enumerated every shape.");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
