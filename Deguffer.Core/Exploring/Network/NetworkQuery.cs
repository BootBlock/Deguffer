namespace Deguffer.Core.Exploring.Network;

/// <summary>Which list what has been typed asks for. See <see cref="NetworkQuery"/>.</summary>
public enum NetworkQueryStage
{
    /// <summary>Nothing to suggest: a local path, or text that is not one.</summary>
    None,

    /// <summary>The box is empty: what this account uses or can see.</summary>
    Everything,

    /// <summary><c>\</c> and part of a computer's name.</summary>
    Computers,

    /// <summary><c>\\server\</c> and part of a share's name.</summary>
    Shares,

    /// <summary><c>\\server\share\...\</c> and part of a folder's name.</summary>
    Folders,
}

/// <summary>
/// What has been typed into the Path… box, read as the list it asks for: the stage, the place that
/// list is read from, and the part of a name that narrows it.
/// </summary>
/// <param name="Stage">See <see cref="NetworkQueryStage"/>.</param>
/// <param name="Parent">The computer for <see cref="NetworkQueryStage.Shares"/>, the folder for <see cref="NetworkQueryStage.Folders"/>, and empty otherwise.</param>
/// <param name="Text">The part of a name after the last separator.</param>
public sealed record NetworkQuery(NetworkQueryStage Stage, string Parent, string Text)
{
    private static readonly NetworkQuery Nothing = new(NetworkQueryStage.None, string.Empty, string.Empty);

    /// <summary>
    /// What <paramref name="typed"/> asks for. Space before it and an opening quote are forgiven, as
    /// the box forgives them when the path is used. A device path (<c>\\?\</c>, <c>\\.\</c>) and a
    /// path with an empty name in it ask for nothing, because no list completes them.
    /// </summary>
    public static NetworkQuery Read(string typed)
    {
        ArgumentNullException.ThrowIfNull(typed);

        var text = typed.TrimStart().TrimStart('"');

        if (text.Length == 0)
        {
            return new NetworkQuery(NetworkQueryStage.Everything, string.Empty, string.Empty);
        }

        if (!text.StartsWith(@"\\", StringComparison.Ordinal) || Safety.LongPath.IsDeviceSpelling(text))
        {
            return Nothing;
        }

        var names = text[2..].Split('\\');

        // Every name but the last is a level already chosen, so none of them may be empty.
        if (names[..^1].Any(name => name.Length == 0))
        {
            return Nothing;
        }

        return names.Length switch
        {
            1 => new NetworkQuery(NetworkQueryStage.Computers, string.Empty, names[0]),
            2 => new NetworkQuery(NetworkQueryStage.Shares, names[0], names[1]),
            _ => new NetworkQuery(NetworkQueryStage.Folders, NetworkNames.Computer(string.Join('\\', names[..^1])), names[^1]),
        };
    }
}
