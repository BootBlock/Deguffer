using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Where Conan's home is, worked out without running Conan, by the rules of
/// <c>get_conan_user_home</c> in <c>conan/internal/paths.py</c>, Conan 2.34.0 (commit
/// <c>70b4cf60b5787ebf1a1af16ac45a11491be0d4c3</c>).
///
/// <para><b>Why not simply ask Conan.</b> Every Conan command builds a <c>ConanAPI</c> first, and that
/// creates the home when it is missing: <c>version.txt</c>, <c>settings.yml</c>, <c>global.conf</c> and
/// a package database. Asking <c>conan config home</c> on a machine where Conan has never been used
/// would leave a home behind that nobody made. So this answer only decides whether there is a home to
/// ask about. Once there is, Conan's own answer is the one the plan uses.</para>
///
/// <para><b>The order.</b> A <c>.conanrc</c> found by walking up from the folder Conan runs in to the
/// root of its drive wins, then <c>CONAN_HOME</c>, then <c>.conan2</c> in the profile. A leading
/// <c>~</c> is the profile, as Python's <c>expanduser</c> reads it on Windows since 3.8, which Conan
/// requires. Anything still not a full path makes Conan stop with an error.</para>
/// </summary>
internal static class ConanHome
{
    /// <summary>The folder in the profile Conan uses when nothing names another.</summary>
    public const string DefaultFolder = ".conan2";

    /// <param name="environment">Whose profile and variables Conan would see.</param>
    /// <param name="workingDirectory">The folder Conan is started in, where its <c>.conanrc</c> search begins.</param>
    /// <param name="walk">How far above that folder the search goes, which for Conan is the drive's root.</param>
    public static ConanHomeAnswer Resolve(IUserEnvironment environment, string workingDirectory, ConanRcWalk walk)
    {
        Naming? named = null;

        switch (walk.Find(workingDirectory))
        {
            case ConanRcSearch.Refused refused:
                return new ConanHomeAnswer.Refused(refused.Path);

            case ConanRcSearch.Found found:
                switch (ConanRc.Read(found.Path))
                {
                    case ConanRcReading.Unusable unusable:
                        return new ConanHomeAnswer.Unknown(unusable.Why);

                    case ConanRcReading.Names names:
                        named = new Naming(names.Home, $"the {ConanRc.FileName} at {found.Path}");
                        break;
                }

                break;
        }

        if (named is null && environment.GetEnvironmentVariable("CONAN_HOME") is { } variable)
        {
            named = new Naming(variable, "the CONAN_HOME variable");
        }

        if (named is not { } naming)
        {
            return new ConanHomeAnswer.Found(Path.Combine(environment.UserProfile, DefaultFolder), "the default");
        }

        return ExpandUser(naming.Value, environment.UserProfile) is { } expanded && LongPath.Configured(expanded) is { } home
            ? new ConanHomeAnswer.Found(home, naming.Source)
            : new ConanHomeAnswer.Unknown(
                $"Conan stops with an error where its home, named by {naming.Source}, is not a full path ('{naming.Value}').");
    }

    /// <summary>
    /// <c>_conan_expand_user</c>. A bare <c>~</c> or one followed by a separator is the profile. One
    /// followed by a name is another user's profile, guessed from this one's parent, which is not
    /// followed here: null, so nothing is assumed about it.
    /// </summary>
    private static string? ExpandUser(string path, string profile) => path switch
    {
        ['~'] => profile,
        ['~', '\\' or '/', ..] => profile + path[1..],
        ['~', ..] => null,
        _ => path,
    };

    /// <summary>A home a setting names, before it is expanded, and which setting named it.</summary>
    private readonly record struct Naming(string Value, string Source);
}

/// <summary>What <see cref="ConanHome.Resolve"/> came to.</summary>
internal abstract record ConanHomeAnswer
{
    /// <summary>Conan's home, in full form, and what named it, for the user.</summary>
    public sealed record Found(string Home, string Source) : ConanHomeAnswer;

    /// <summary>Windows would not say whether a <c>.conanrc</c> is at <paramref name="Path"/>.</summary>
    public sealed record Refused(string Path) : ConanHomeAnswer;

    /// <summary>Which home Conan would use cannot be known, or Conan would refuse to start, for the reason given.</summary>
    public sealed record Unknown(string Why) : ConanHomeAnswer;
}
