using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// The shared tree under one Affinity profile root, as it was found on disk.
/// </summary>
/// <param name="Root">The profile root itself — <c>%USERPROFILE%\.affinity</c> or <c>%APPDATA%\Affinity</c>.</param>
/// <param name="Common">
/// The <c>Common</c> folder inside it, or null where there is none. Affinity's products share it,
/// and it is never a target: the version folders in it hold the user's whole asset library.
/// </param>
/// <param name="Versions">
/// The children of <paramref name="Common"/> whose name is a major version, which are the folders a
/// model cache can sit in. Never targets themselves.
/// </param>
/// <param name="Unrecognised">
/// The children of <paramref name="Common"/> that are not. Reported so the plan can say what it is
/// leaving alone rather than letting the total quietly disagree with the folder.
/// </param>
/// <param name="Links">Children of <paramref name="Common"/> that are junctions or symbolic links.</param>
/// <param name="Unreadable">Whether <paramref name="Common"/> refused to be listed.</param>
/// <param name="LinkedAway">
/// The path that turned out to be a link rather than a directory — the root or <c>Common</c> —
/// or null where neither was. Nothing below it was classified, so nothing below it is reported.
/// </param>
public sealed record AffinityCommonTree(
    string Root,
    string? Common,
    IReadOnlyList<DirectoryInfo> Versions,
    IReadOnlyList<DirectoryInfo> Unrecognised,
    IReadOnlyList<DirectoryInfo> Links,
    bool Unreadable,
    string? LinkedAway)
{
    /// <summary>
    /// The folder Windows would not describe, or null where it described every one of them. Apart
    /// from <see cref="Unreadable"/>, which is a folder that was reached and would not be listed:
    /// that sentence asserts the folder exists, and this one cannot.
    /// </summary>
    public string? Unreached { get; init; }
}

/// <summary>
/// Finds Affinity's shared per-version folders, under both of the roots the suite has used.
///
/// <para>Separate from <see cref="AffinityModelCacheProvider"/> on
/// <see cref="ChromiumUserDataDiscovery"/>'s reasoning: this answers "where does Affinity keep its
/// shared state", and the provider answers "what inside it may go". The second question is the
/// dangerous one, and keeping it in one place is what stops it being asked of a folder that never
/// passed the first.</para>
///
/// <para><b>Two roots, because the location moved.</b> Affinity 1 kept its shared folder under
/// <c>%APPDATA%\Affinity</c>, Affinity 2 moved it to <c>%USERPROFILE%\.affinity</c>, and Affinity 3
/// moved it back. Both are live on a machine that has run more than one major version, and an
/// uninstalled version leaves its tree behind — so neither root is evidence about the other.</para>
/// </summary>
public static partial class AffinityProfiles
{
    /// <summary>Affinity's folder name under <c>%APPDATA%</c>, and the stem of the one in the profile.</summary>
    public const string RoamingFolderName = "Affinity";

    /// <summary>Affinity 2's folder in the profile, which is hidden and dot-prefixed.</summary>
    public const string ProfileFolderName = ".affinity";

    /// <summary>The folder Affinity's products share, one level under a profile root.</summary>
    public const string CommonName = "Common";

    /// <summary>
    /// A shared version folder, whose whole name is a major version — <c>1.0</c>, <c>2.0</c>,
    /// <c>3.0</c>.
    ///
    /// <para><b>Exactly two parts, which is the shape Affinity has always written.</b> The rule
    /// decides which folders Deguffer looks inside for a model cache, so a wider one would let a
    /// <c>modelcache</c> under a folder somebody made by hand become a target. A version Serif
    /// numbers differently one day stops being offered and is named in the plan as something left
    /// alone, which is visible and fixable — where the opposite mistake is not.</para>
    ///
    /// Anchored with <c>\A</c> and <c>\z</c> rather than <c>^</c> and <c>$</c>: <c>$</c> also matches
    /// before a trailing newline, and a check that decides whether Deguffer looks inside a folder
    /// holding an asset library should admit no such reading.
    /// </summary>
    [GeneratedRegex(@"\A[0-9]+\.[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex MajorVersion();

    /// <summary>
    /// The two profile roots, whether or not they are on this machine. Named rather than found,
    /// because they are the only two Affinity has ever used.
    /// </summary>
    public static IReadOnlyList<string> RootsFor(IUserEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return
        [
            Path.Combine(environment.UserProfile, ProfileFolderName),
            Path.Combine(environment.RoamingAppData, RoamingFolderName),
        ];
    }

    /// <summary>
    /// One entry per profile root that is on this machine, each classified far enough for a caller
    /// to decide what may go inside it.
    ///
    /// <para>A root that is not there yields no entry at all: absence is a complete answer, and an
    /// entry for it would put "Deguffer is leaving this alone" against a folder nobody has.</para>
    /// </summary>
    public static IReadOnlyList<AffinityCommonTree> Discover(
        IUserEnvironment environment,
        CancellationToken ct = default)
    {
        var found = new List<AffinityCommonTree>();

        foreach (var root in RootsFor(environment))
        {
            ct.ThrowIfCancellationRequested();

            switch (LongPath.ProbeDirectory(root))
            {
                // Windows would not say whether the root is there, so nothing under it was examined
                // and dropping it would let the plan report Affinity as never run on a machine that
                // has run it. Unreached rather than Unreadable: that sentence asserts the folder
                // exists, and nothing here established it.
                case PathPresence.Refused:
                    found.Add(new AffinityCommonTree(
                        root, null, [], [], [], Unreadable: false, LinkedAway: null) { Unreached = root });
                    continue;

                case PathPresence.Absent:
                    continue;
            }

            found.Add(Classify(root, ct));
        }

        return found;
    }

    private static AffinityCommonTree Classify(string root, CancellationToken ct)
    {
        // Moving a profile folder onto another drive with a junction is ordinary, and the
        // enumeration below never classifies the directory it is handed: it would hand back the far
        // side's children, and every §5.6 assertion named for this root would pass because each
        // survivor resolves through the same link. Both levels can be moved that way, so both are
        // asked.
        if (LongPath.IsReparsePoint(root))
        {
            return new AffinityCommonTree(root, null, [], [], [], Unreadable: false, LinkedAway: root);
        }

        var common = Path.Combine(root, CommonName);

        switch (LongPath.ProbeDirectory(common))
        {
            // Common stays null, because nothing below it was classified. The caller asserts it
            // survived through Unreached, as a path Windows would not describe.
            case PathPresence.Refused:
                return new AffinityCommonTree(root, null, [], [], [], Unreadable: false, LinkedAway: null)
                {
                    Unreached = common,
                };

            case PathPresence.Absent:
                return new AffinityCommonTree(root, null, [], [], [], Unreadable: false, LinkedAway: null);
        }

        if (LongPath.IsReparsePoint(common))
        {
            return new AffinityCommonTree(root, common, [], [], [], Unreadable: false, LinkedAway: common);
        }

        var scan = ChildDirectories.Under(common);
        var versions = new List<DirectoryInfo>();
        var unrecognised = new List<DirectoryInfo>();

        foreach (var child in scan.Directories)
        {
            ct.ThrowIfCancellationRequested();

            (MajorVersion().IsMatch(child.Name) ? versions : unrecognised).Add(child);
        }

        return new AffinityCommonTree(
            root,
            common,
            versions,
            unrecognised,
            scan.Links,
            scan.Unreadable,
            LinkedAway: null);
    }
}
