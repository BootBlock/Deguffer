using Deguffer.Core.Safety;

namespace Deguffer.Core.Exploring.Acting;

/// <summary>
/// Every path Explore refuses to remove as a path of its own, asked about from above: a folder that
/// holds one of them is refused too, because removing a folder removes everything in it (§7.1).
///
/// <para>The region table and §5.2 each answer about the path they are handed and what contains it.
/// Neither can see what the path contains, so <c>%LOCALAPPDATA%\Microsoft</c> was allowed while
/// Visual Studio's folder inside it was refused, and removing the first took the second with it.
/// That is the refused deletion one level up, and this is the question that catches it.</para>
///
/// <para><b>Only a location that is on disk refuses.</b> A folder holding nothing Explore refuses
/// takes nothing protected with it, and a refusal naming a folder that is not there would be untrue —
/// the folder a tool leaves behind after somebody uninstalls it is exactly the kind of thing Explore
/// is for. <see cref="IFileSystem.MayExist"/> fails closed, so a location that could not be asked
/// about counts as present.</para>
///
/// <para>The disk is asked only about locations the path holds, shallowest first, and the first
/// one present settles it. The shell asks on every change of selection, so a folder high in the
/// profile costs a handful of probes rather than one per declaration.</para>
/// </summary>
internal sealed class HeldLocations
{
    private readonly IReadOnlyList<(string Path, string Reason)> _locations;
    private readonly IFileSystem _fileSystem;

    /// <param name="locations">
    /// Resolved paths in display form, and the reason each is refused. Where one path is refused for
    /// two reasons the first is kept, as <see cref="ExploreActionPolicy"/> shows the first refusal of
    /// a path owned twice.
    /// </param>
    public HeldLocations(IEnumerable<(string Path, string Reason)> locations, IFileSystem fileSystem)
    {
        _locations =
        [
            .. locations
                .DistinctBy(l => l.Path, StringComparer.OrdinalIgnoreCase)
                .OrderBy(l => l.Path.Length),
        ];

        _fileSystem = fileSystem;
    }

    /// <summary>The refusal for removing <paramref name="target"/>, or null where it holds nothing refused.</summary>
    /// <param name="target">A resolved path in display form, as the policy has already made it.</param>
    public ExploreVerdict? Refusal(string target)
    {
        foreach (var (location, reason) in _locations)
        {
            // Strictly inside: a location as long as the target is the target itself, which the
            // passes before this one have already answered.
            if (location.Length <= target.Length || !LongPath.Contains(target, location))
            {
                continue;
            }

            if (_fileSystem.MayExist(LongPath.Extended(location)))
            {
                return ExploreVerdict.Refuse(
                    $"'{Path.GetFileName(target)}' holds '{location}', and removing a folder removes "
                    + $"everything in it. Explore refuses '{Path.GetFileName(location)}' itself: {reason}");
            }
        }

        return null;
    }
}
