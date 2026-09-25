using Deguffer.Core.Safety;

namespace Deguffer.Core.Cloud;

/// <summary>
/// The pin each folder under one sync root passes to what is inside it, read from the disk and kept
/// for the life of one clean (G4): a thousand files in one folder ask about that folder once.
///
/// <para><b>Read again at the clean rather than carried from the preview.</b> A user who pins a folder
/// while the preview sits on screen has just said its files stay, and the files inside record nothing
/// of it: a scratch sync root showed a file under a pinned folder still reporting no pin of its own.
/// </para>
/// </summary>
public sealed class InheritedPins(ICloudFiles cloud, string syncRoot)
{
    private readonly Dictionary<string, PinState> _passedOn = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _root = Path.TrimEndingDirectorySeparator(LongPath.Display(syncRoot));

    /// <summary>The pin a file directly inside <paramref name="directory"/> inherits.</summary>
    public PinState For(string directory)
    {
        var folder = Path.TrimEndingDirectorySeparator(LongPath.Display(directory));

        if (_passedOn.TryGetValue(folder, out var known))
        {
            return known;
        }

        // Nothing above the sync root passes a pin into it, and a folder outside it is not one any
        // plan names.
        var inherited = !string.Equals(folder, _root, StringComparison.OrdinalIgnoreCase)
            && LongPath.Contains(_root, folder)
            && Path.GetDirectoryName(folder) is { } parent
                ? For(parent)
                : PinState.Unspecified;

        return _passedOn[folder] = Own(cloud, folder, inherited);
    }

    /// <summary>
    /// What <paramref name="folder"/> passes on, given what it inherited.
    ///
    /// <para>A folder Windows would not describe passes on <see cref="PinState.Pinned"/>, which leaves
    /// everything inside it alone: nobody can say the user did not pin it.</para>
    /// </summary>
    public static PinState Own(ICloudFiles cloud, string folder, PinState inherited)
    {
        ArgumentNullException.ThrowIfNull(cloud);

        var reading = cloud.Read(folder);

        return reading.Presence is PathPresence.Refused
            ? PinState.Pinned
            : ReleaseRules.PassedOn(reading.Placeholder?.Pin ?? PinState.Unspecified, inherited);
    }
}
