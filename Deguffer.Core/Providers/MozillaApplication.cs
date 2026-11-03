using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What distinguishes one Mozilla application's profiles from another's, for
/// <see cref="MozillaProfileCacheProvider"/>. Everything else about a Mozilla profile — the register,
/// the two roots, the derived local path and the links along it — is the engine's, and identical.
/// </summary>
/// <param name="Name">The application's name as the user knows it, which every sentence uses.</param>
/// <param name="Directory">
/// The application's own directory relative to each application-data root, which
/// <see cref="MozillaProfileDiscovery"/> resolves the register and both halves of a profile under.
/// </param>
/// <param name="Children">
/// What may be removed from a profile's <em>local</em> half. Anything not named here is Tier 4 by
/// construction, which is the direction §5.2 requires the unknown case to fail in.
/// </param>
/// <param name="UserData">
/// The user data in the roaming half, named in full rather than sampled. Child classification
/// enumerates directories in the local half, so nothing in the roaming half is ever seen, classified
/// or asserted unless it is named here, and an assertion that the profile directory survived would
/// pass with every one of them gone.
/// </param>
/// <param name="UserDataSummary">
/// The same user data in a few words, lower case, for the sentences that refuse the roaming half:
/// <c>bookmarks, history, saved passwords and settings</c>.
/// </param>
/// <param name="Kept">
/// The one recognised directory in the local half that is measured and reported rather than offered,
/// where the application has one.
/// </param>
public sealed record MozillaApplication(
    string Name,
    string Directory,
    DisposableChildSet Children,
    IReadOnlyList<MozillaUserData> UserData,
    string UserDataSummary,
    MozillaKeptData? Kept);

/// <summary>One named piece of user data in the roaming half of a profile.</summary>
/// <param name="Name">The file or directory's name in the profile.</param>
/// <param name="Reason">What it is, written for the user.</param>
public sealed record MozillaUserData(string Name, string Reason);

/// <summary>
/// A directory in the local half that <see cref="MozillaApplication.Children"/> declares at Tier 4
/// and the plan measures, because it is most of the profile and a plan that only said "left alone"
/// would leave the user unable to account for the space.
/// </summary>
/// <param name="Name">The directory's name, which the child table also declares.</param>
/// <param name="What">What the directory holds, as it reads after "keeps 1.5 GB of".</param>
/// <param name="Why">
/// The rest of the sentence after the size: what most of it is and why it is not offered.
/// </param>
public sealed record MozillaKeptData(string Name, string What, string Why);
