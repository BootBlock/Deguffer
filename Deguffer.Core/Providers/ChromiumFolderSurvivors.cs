namespace Deguffer.Core.Providers;

/// <summary>
/// What §5.6 asserts survives in a Chromium user-data folder a row looks inside, besides what its walk
/// spared: the folder and each profile themselves, and the files beside the caches that hold the
/// user's settings and sign-ins, which no walk enumerates. See <see cref="ChromiumUserDataProvider"/>.
/// </summary>
internal static class ChromiumFolderSurvivors
{
    /// <summary>Why a folder a running program is using is asserted to survive whole.</summary>
    public const string InUseReason =
        "A running program is using this folder, so nothing inside it is removed.";

    /// <summary>
    /// Why the file that identified the user-data folder must survive. §5.6 asserts it by name
    /// because a <see cref="DisposableChildSet"/> only ever classifies a directory, so a file beside
    /// the caches is never enumerated, never classified, and never asserted unless it is named —
    /// the lesson NVIDIA's <c>accounts</c> taught, in a folder with a great deal more to lose. Which
    /// file that is belongs to the folder's <see cref="ChromiumLayout"/>.
    /// </summary>
    private const string IdentifyingFileReason =
        "The application's own settings, and the key that decrypts its saved cookies and passwords.";

    /// <summary>
    /// Why a framework host's partition keeps its own copy of the identifying file. It is what
    /// made the directory a profile, and it holds that partition's settings.
    /// </summary>
    private const string PartitionFileReason = "The settings of this part of the application's browser.";

    /// <summary>
    /// The same, per profile: the credential surface, named in full rather than sampled. Anything
    /// less makes the §5.6 evidence weaker than the claim it supports.
    ///
    /// <c>Cookies</c> is listed at both paths because Chromium moved it under <c>Network</c> and
    /// older profiles still keep it at the top level. Whichever is absent records itself as nothing
    /// to preserve rather than as a pass.
    /// </summary>
    private static readonly (string Name, string Reason)[] ProtectedProfileFiles =
    [
        ("Cookies", "Sign-in cookies. Removing them signs the user out of everything."),
        (@"Network\Cookies", "Sign-in cookies. Removing them signs the user out of everything."),
        ("Login Data", "Saved usernames and passwords."),
        ("Web Data", "Saved addresses and payment cards."),
    ];

    /// <summary>The user-data folder of <paramref name="application"/>, and the file that identified it.</summary>
    public static IEnumerable<(string Path, string Reason)> OfFolder(ChromiumUserData application)
    {
        yield return (
            application.Path,
            $"The '{application.Name}' data folder itself must survive — only recognised cache directories inside it are removed.");
        yield return (Path.Combine(application.Path, application.Layout.IdentifyingFile), IdentifyingFileReason);
    }

    /// <summary>
    /// <paramref name="profile"/> of <paramref name="application"/>, its credential files, and, in a
    /// framework host's partition, its own copy of the identifying file.
    /// </summary>
    public static IEnumerable<(string Path, string Reason)> OfProfile(ChromiumUserData application, string profile)
    {
        yield return (
            profile,
            "The profile directory itself must survive — only recognised cache directories inside it are removed.");

        foreach (var (name, reason) in ProtectedProfileFiles)
        {
            yield return (Path.Combine(profile, name), reason);
        }

        if (application.Layout.Profiles is ChromiumProfileRule.Marked
            && !profile.Equals(application.Path, StringComparison.OrdinalIgnoreCase))
        {
            yield return (Path.Combine(profile, application.Layout.IdentifyingFile), PartitionFileReason);
        }
    }
}
