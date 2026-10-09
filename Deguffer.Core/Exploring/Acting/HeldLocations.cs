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
/// <para><b>Asked of folders, not of their text.</b> A location is named the way its provider or
/// Windows named it, and the target the way the user reached it. With <c>S:</c> substituted for
/// <c>C:\Users\testuser\src</c>, removing that folder removes a vcpkg clone declared at
/// <c>S:\vcpkg</c>, and compared as text it held nothing. Each location arrives followed to every
/// path it is reachable at, once, when the policy is built, for the reason <see cref="DeclaredRoot"/>
/// gives.</para>
///
/// <para>The disk is asked only about locations the path holds, shallowest first, and the first
/// one present settles it. The shell asks on every change of selection, so a folder high in the
/// profile costs a handful of probes rather than one per declaration.</para>
/// </summary>
internal sealed class HeldLocations
{
    private readonly IReadOnlyList<(string Reason, ReachedFolder Folder)> _locations;
    private readonly IFileSystem _fileSystem;

    /// <param name="locations">
    /// The reason each location is refused, and the folder it is. Where one folder is refused for two
    /// reasons the first is shown, as <see cref="ExploreActionPolicy"/> shows the first refusal of a
    /// path owned twice.
    /// </param>
    public HeldLocations(IEnumerable<(string Reason, ReachedFolder Folder)> locations, IFileSystem fileSystem)
    {
        _locations = [.. locations];

        _fileSystem = fileSystem;
    }

    /// <summary>The refusal for removing <paramref name="target"/>, or null where it holds nothing refused.</summary>
    /// <param name="target">A resolved path in display form, as the policy has already made it.</param>
    /// <param name="folder">The folder at <paramref name="target"/>, at every path it is reachable at.</param>
    public ExploreVerdict? Refusal(string target, ReachedFolder folder)
    {
        // Strictly inside: a location no levels down is the target itself, which the passes before
        // this one have already answered. Shallowest first by levels rather than by the length of the
        // location's path, which says nothing where the two are named through different letters.
        var held = _locations
            .Select(location => (Location: location, Levels: folder.LevelsTo(location.Folder)))
            .Where(location => location.Levels > 0)
            .OrderBy(location => location.Levels);

        foreach (var ((reason, located), _) in held)
        {
            // Asked about at every path it is reachable at, and present where any answers so: each is
            // the same folder, and the path it was declared at may be a letter or a mount that no
            // longer answers. Named below the target in the refusal, because that is the path the
            // removal takes it at, and the one the user reached it by.
            if (located.Places.Any(place => _fileSystem.MayExist(LongPath.Extended(place))))
            {
                var location = folder.Naming(located, target)!;

                return ExploreVerdict.Refuse(
                    $"'{Path.GetFileName(target)}' holds '{location}', and removing a folder removes "
                    + $"everything in it. Deguffer refuses '{Path.GetFileName(location)}' itself: {reason}");
            }
        }

        return null;
    }
}
