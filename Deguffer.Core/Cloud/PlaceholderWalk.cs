using Deguffer.Core.Safety;

namespace Deguffer.Core.Cloud;

/// <summary>
/// One sync root walked for the placeholders whose local copies may be released, and a tally of those
/// left as they are and why.
///
/// <para><b>What it opens, and what it never does.</b> Each folder is listed from its own index, and only
/// an entry that index calls a placeholder is opened, for its attributes. A file's data is never read, so
/// the walk cannot start a download.</para>
///
/// <para><b>Links are neither entered nor offered.</b> A junction or symbolic link inside a sync root
/// leads somewhere the sync app does not own, for the reason <see cref="Execution.DirectoryRemover"/>
/// does not follow one.</para>
/// </summary>
public static class PlaceholderWalk
{
    public static ReleaseSelection Of(ICloudFiles cloud, string syncRoot, MinimumAge keep, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cloud);

        var files = new List<ReleasableFile>();
        var held = new Dictionary<HeldBack, HeldTally>();
        var unreadable = 0;

        var pending = new Stack<(string Directory, PinState Inherited)>();
        pending.Push((syncRoot, InheritedPins.Own(cloud, syncRoot, PinState.Unspecified)));

        while (pending.TryPop(out var folder))
        {
            foreach (var entry in cloud.List(folder.Directory, ct))
            {
                if (entry.IsOtherLink || !entry.IsPlaceholder)
                {
                    // A folder that is not a placeholder is still inside the root, and a sync app may
                    // keep placeholders below one. A link is the one thing never entered.
                    if (entry.IsDirectory && !entry.IsOtherLink)
                    {
                        pending.Push((entry.Path, folder.Inherited));
                    }

                    continue;
                }

                var reading = cloud.Read(entry.Path);

                if (reading.Placeholder is not { } placeholder)
                {
                    unreadable += reading.Presence is PathPresence.Refused ? 1 : 0;
                    continue;
                }

                if (entry.IsDirectory)
                {
                    pending.Push((entry.Path, ReleaseRules.PassedOn(placeholder.Pin, folder.Inherited)));
                    continue;
                }

                switch (ReleaseRules.Hold(placeholder, folder.Inherited, keep))
                {
                    case null:
                        files.Add(new ReleasableFile(entry.Path, placeholder.OnDiskBytes));
                        break;

                    case HeldBack.NothingOnDisk:
                        break;

                    case { } reason:
                        held[reason] = held.GetValueOrDefault(reason) + placeholder.OnDiskBytes;
                        break;
                }
            }
        }

        return new ReleaseSelection(files, held, unreadable);
    }
}

/// <summary>A placeholder chosen for release, and the bytes of it on this PC when it was chosen.</summary>
public sealed record ReleasableFile(string Path, long OnDiskBytes);

/// <summary>How many files one reason held back, and what they hold on this PC.</summary>
public readonly record struct HeldTally(int Files, long Bytes)
{
    public static HeldTally operator +(HeldTally tally, long bytes) => new(tally.Files + 1, tally.Bytes + bytes);
}

/// <summary>What <see cref="PlaceholderWalk"/> found in one sync root.</summary>
/// <param name="Files">The placeholders that may be released.</param>
/// <param name="Held">Those with something on this PC that the rules left as they are, by reason.</param>
/// <param name="Unreadable">
/// Placeholders Windows would not describe, files and folders alike, which are left as they are with
/// everything inside them.
/// </param>
public sealed record ReleaseSelection(
    IReadOnlyList<ReleasableFile> Files,
    IReadOnlyDictionary<HeldBack, HeldTally> Held,
    int Unreadable)
{
    public long Bytes => Files.Sum(f => f.OnDiskBytes);

    public HeldTally HeldFor(HeldBack reason) => Held.GetValueOrDefault(reason);
}
