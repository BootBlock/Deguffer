using System.Collections.Concurrent;
using Deguffer.Core.Scanning;

namespace Deguffer.Testing;

/// <summary>
/// Answers what a file occupies from a table, by its name, and records every file it was asked
/// about, so a test can show which files the walk measured and which it took at their length.
/// A file it has no answer for is one Windows would not measure.
/// </summary>
public sealed class FakeOccupancyProbe : IOccupancyProbe
{
    private readonly Dictionary<string, Occupancy> _answers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _asked = new();

    /// <summary>The paths asked about, in the form the walk passed them.</summary>
    public IReadOnlyCollection<string> Asked => _asked;

    /// <summary>Say that the file called <paramref name="name"/> occupies <paramref name="bytes"/>.</summary>
    public FakeOccupancyProbe Occupying(string name, long bytes)
    {
        _answers[name] = new Occupancy(bytes, IsLink: false);
        return this;
    }

    /// <summary>Say that the file called <paramref name="name"/> is a symbolic link.</summary>
    public FakeOccupancyProbe Linking(string name)
    {
        _answers[name] = new Occupancy(0, IsLink: true);
        return this;
    }

    public Occupancy? Measure(string path, FileAttributes attributes)
    {
        _asked.Enqueue(path);

        return _answers.TryGetValue(Path.GetFileName(path), out var answer) ? answer : null;
    }
}
