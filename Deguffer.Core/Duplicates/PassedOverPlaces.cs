using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Duplicates;

/// <summary>A place a search did not go into, and why, so it is never read as a place that held nothing.</summary>
public sealed record PassedOverPlace(string Path, string Reason);

/// <summary>
/// The places a duplicate search passes over by default (§7.4): what Explore refuses at and below,
/// and the folders programs are installed in.
///
/// <para><b>Explore's refusals are asked of Explore's policy</b>, through the member that answers
/// for a place and everything in it, so the list cannot drift from what Explore refuses:
/// <c>C:\Windows</c>, the program folders, <c>%PROGRAMDATA%</c>, other accounts' profiles, every
/// Recycle Bin and what Windows keeps at the top of a volume. The signed-in profile is carved out of
/// the Users folder there, and so is searched here.</para>
///
/// <para><b>Asked about a handful of folders, not each.</b> A drive holds hundreds of thousands of
/// folders, and the policy asks the machine where each one is mounted. Its
/// <see cref="RefusalWatch"/> says which children can be refused where their folder was not, and a
/// program folder can only start below a folder on the way to one, so every other child is passed
/// over or searched as its folder was.</para>
/// </summary>
internal sealed class PassedOverPlaces
{
    private readonly ExploreActionPolicy _policy;
    private readonly IReadOnlyList<ProgramFolder> _programs;
    private readonly IReadOnlyList<string> _programPlaces;

    public PassedOverPlaces(ExploreActionPolicy policy, IReadOnlyList<ProgramFolder> programs)
    {
        _policy = policy;
        _programs = programs;
        _programPlaces = [.. programs.SelectMany(program => program.Reached.Places)];
    }

    /// <summary>Why <paramref name="path"/> is passed over with everything in it, or null where it is searched.</summary>
    public string? WhyPassedOver(string path) =>
        _policy.RefusedAtAndBelow(path)?.Reason ?? ProgramFolderAt(path);

    /// <summary>The questions to ask below <paramref name="root"/>, a folder that is searched.</summary>
    public Below Within(string root) => new(this, _policy.WatchBelow(root));

    private string? ProgramFolderAt(string path) =>
        _programs.FirstOrDefault(program => program.Reached.Places.Any(place => LongPath.Contains(place, path)))
            is { } held
            ? $"'{held.Program}' is installed here. Two programs that ship the same file each need their "
              + "own copy, so a search passes over the folders programs are installed in."
            : null;

    /// <summary>What is passed over below one searched folder.</summary>
    internal sealed class Below(PassedOverPlaces places, RefusalWatch watch)
    {
        /// <summary>
        /// Why the child <paramref name="child"/>, named <paramref name="childName"/>, of the searched
        /// folder <paramref name="parent"/> is passed over with everything in it, or null where it is
        /// searched as its folder was.
        /// </summary>
        public string? WhyPassedOver(string parent, bool parentIsRoot, string child, string childName)
        {
            if (watch.MayRefuse(parent, parentIsRoot, childName) && places._policy.RefusedAtAndBelow(child) is { } refused)
            {
                return refused.Reason;
            }

            return places._programPlaces.Any(place => LongPath.Contains(parent, place))
                ? places.ProgramFolderAt(child)
                : null;
        }
    }
}
