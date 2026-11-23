using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>What the user's <c>settings.xml</c> says about Maven's local repository.</summary>
/// <param name="LocalRepository">See <see cref="MavenRepositoryProvider.ResolveLocalRepository"/>.</param>
/// <param name="Unread">
/// The file where it could not be read, or Windows would not say whether it is there, so a
/// repository it moves elsewhere was never looked for. <paramref name="LocalRepository"/> is then
/// the default.
/// </param>
internal sealed record MavenUserSettings(string? LocalRepository, UnreadFile? Unread)
{
    /// <summary>
    /// The one property this reads from a configured path. Maven interpolates its whole property
    /// set into <c>localRepository</c>, which Deguffer cannot reproduce, but this one is the common
    /// idiom for writing a portable settings file and resolving it is exact rather than a guess.
    /// A value naming any other property is left unresolved and the provider says so.
    /// </summary>
    private const string UserHomeProperty = "${user.home}";

    /// <summary>Reads the user's <c>settings.xml</c>, falling back on the default local repository.</summary>
    /// <param name="settingsPath">The user's <c>settings.xml</c>, in the Maven home.</param>
    /// <param name="defaultRepository">Where Maven keeps the local repository when nothing has moved it.</param>
    /// <param name="userProfile">What <c>${user.home}</c> resolves to.</param>
    public static MavenUserSettings Read(string settingsPath, string defaultRepository, string userProfile)
    {
        switch (LongPath.ProbeFile(settingsPath))
        {
            case PathPresence.Absent:
                return new MavenUserSettings(defaultRepository, Unread: null);

            case PathPresence.Refused:
                return new MavenUserSettings(defaultRepository, new UnreadFile(settingsPath, Unreached: true));
        }

        if (ReadConfiguredRepository(settingsPath, userProfile, out var unreadable) is not { } configured)
        {
            return new MavenUserSettings(
                defaultRepository,
                unreadable ? new UnreadFile(settingsPath, Unreached: false) : null);
        }

        // Normalised rather than used as it arrived. A trailing separator would make the leaf name
        // empty, and a location with an empty relative path resolves back to the root that holds it
        // — so the plan would target the very directory it also asserts must survive, and §5.6 would
        // report a correct run as a failure. A value ending in '..' is worse: LongPath.Extended
        // requires an already-resolved path, so the removal would land a directory higher than the
        // plan named.
        return new MavenUserSettings(LongPath.Configured(configured), Unread: null);
    }

    /// <summary>
    /// The <c>localRepository</c> element of the user's settings file, or null if there is not one.
    ///
    /// Matched on the local name, because Maven's own schema puts the file in a namespace and a
    /// great many real settings files omit it. A malformed file is treated as no override rather
    /// than as an error: Maven itself would refuse to build, which the user will hear about from
    /// Maven, and the default location is the right thing for Deguffer to fall back on either way.
    ///
    /// <para>A file that would not be read is different, and <paramref name="unreadable"/> says so.
    /// Maven may read it where Deguffer cannot, so <see cref="Read"/> still examines the
    /// default, and the plan says the settings were not read.</para>
    /// </summary>
    private static string? ReadConfiguredRepository(string settingsPath, string userProfile, out bool unreadable)
    {
        var configured = XmlFile
            .TryLoad(settingsPath, out unreadable)?
            .Root?
            .Elements()
            .FirstOrDefault(e => e.Name.LocalName == "localRepository")?
            .Value
            .Trim();

        if (string.IsNullOrEmpty(configured))
        {
            return null;
        }

        return configured.StartsWith(UserHomeProperty, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(userProfile, configured[UserHomeProperty.Length..].TrimStart('/', '\\'))
            : configured;
    }
}
