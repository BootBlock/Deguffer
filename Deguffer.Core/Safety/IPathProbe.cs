namespace Deguffer.Core.Safety;

/// <summary>
/// The three presence questions a rule asks of a path, behind a seam so a rule that turns an
/// absence into a verdict is proved against a link, a refusal or an unplugged drive a test
/// describes, rather than one the test machine happens to have.
/// </summary>
public interface IPathProbe
{
    /// <summary>See <see cref="LongPath.ProbeFile"/>.</summary>
    PathPresence ProbeFile(string path);

    /// <summary>See <see cref="LongPath.ProbeEntry"/>.</summary>
    PathPresence ProbeEntry(string path);

    /// <summary>See <see cref="LongPath.ProbeDirectory(string, out bool?)"/>.</summary>
    PathPresence ProbeDirectory(string path, out bool? isLink);
}

/// <inheritdoc />
public sealed class LongPathProbe : IPathProbe
{
    /// <summary>The one instance the app runs with (G5).</summary>
    public static LongPathProbe Default { get; } = new();

    private LongPathProbe()
    {
    }

    public PathPresence ProbeFile(string path) => LongPath.ProbeFile(path);

    public PathPresence ProbeEntry(string path) => LongPath.ProbeEntry(path);

    public PathPresence ProbeDirectory(string path, out bool? isLink) => LongPath.ProbeDirectory(path, out isLink);
}
