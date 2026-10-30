using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Where a Scoop keeps its root, its cache and its settings, worked out in the order Scoop's own
/// start-up works them out (<c>lib/core.ps1</c>, "Core Bootstrap", at
/// <c>e6aa3b366bdee8ed138c1e0f7b85192ebdd35d0f</c>):
///
/// <list type="number">
/// <item>The settings file is <c>$env:XDG_CONFIG_HOME</c>, else <c>$env:USERPROFILE\.config</c>,
/// followed by <c>scoop\config.json</c>; but where Scoop runs from <c>&lt;root&gt;\apps\scoop\current</c>
/// and <c>&lt;root&gt;\config.json</c> exists, that portable file is read instead.</item>
/// <item>The root, <c>$scoopdir</c>, is <c>$env:SCOOP</c>, else the <c>root_path</c> setting, else four
/// folders above Scoop's <c>lib</c>, which is the install's root.</item>
/// <item>The cache, <c>$cachedir</c>, is <c>$env:SCOOP_CACHE</c>, else the <c>cache_path</c> setting,
/// else <c>cache</c> in the root.</item>
/// </list>
///
/// <para><b>Mirrored rather than asked for, because nothing in Scoop prints the cache folder.</b>
/// <c>scoop config cache_path</c> prints only the setting, and <c>scoop cache show</c> only what the
/// folder holds. Where any step cannot be mirrored exactly, the whole answer is declined: the plan
/// examines the folder this names, and the command empties the folder Scoop names, so the two must be
/// one folder.</para>
///
/// <para>An unset <c>XDG_CONFIG_HOME</c> falls through because PowerShell's <c>Select-Object</c> skips
/// a null input, which was observed under Windows PowerShell 5.1 and PowerShell 7. A set one is not
/// checked for being empty: only <c>Where-Object { $_ }</c> passes over that, and this list has
/// none.</para>
/// </summary>
internal static class ScoopLocations
{
    /// <summary>The layout of the Scoop that <paramref name="command"/> starts.</summary>
    public static ScoopResolution Resolve(IUserEnvironment environment, string command)
    {
        if (ScoopCommand.InstallRoot(command, out var why) is not { } installRoot)
        {
            return ScoopResolution.Unfollowed(why);
        }

        if (ConfigHome(environment, out why) is not { } configHome)
        {
            return ScoopResolution.Unfollowed(why);
        }

        var userConfig = Path.Combine(configHome, "scoop", "config.json");
        var portableConfig = Path.Combine(installRoot, "config.json");

        if (ChosenConfig(portableConfig, userConfig, out why) is not { } config)
        {
            return ScoopResolution.Unfollowed(why);
        }

        if (ScoopPath.HasWildcard(config))
        {
            return ScoopResolution.Unfollowed(Unfollowed($"Scoop's settings file is at '{config}'"));
        }

        if (ScoopSettings.Read(config, out why) is not { } settings)
        {
            return ScoopResolution.Unfollowed(why);
        }

        if (First("Scoop's root", out why, Variable(environment, "SCOOP"), settings.RootPath, ScoopValue.From(installRoot))
            is not { } root)
        {
            return ScoopResolution.Unfollowed(why);
        }

        if (First("Scoop's cache", out why, Variable(environment, "SCOOP_CACHE"), settings.CachePath, ScoopValue.From(Path.Combine(root, "cache")))
            is not { } cache)
        {
            return ScoopResolution.Unfollowed(why);
        }

        return ScoopResolution.Of(new ScoopLayout(command, root, cache, [portableConfig, userConfig]));
    }

    private static string? ConfigHome(IUserEnvironment environment, out string why)
    {
        why = string.Empty;

        if (environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is not { } configured)
        {
            return Path.Combine(environment.UserProfile, ".config");
        }

        var home = ScoopPath.Followed(configured);

        if (home is null)
        {
            why = Unfollowed($"XDG_CONFIG_HOME, which Scoop reads its settings from, is '{configured}'");
        }

        return home;
    }

    /// <summary>
    /// The portable file where Scoop's <c>Test-Path</c> would find one, which is true of a folder by
    /// that name too. Scoop then fails to read it and carries on with no settings, which
    /// <see cref="ScoopSettings.Read"/> declines.
    /// </summary>
    private static string? ChosenConfig(string portable, string user, out string why)
    {
        why = string.Empty;

        switch (LongPath.ProbeEntry(portable))
        {
            case PathPresence.Present:
                return portable;

            case PathPresence.Refused:
                why = $"{new UnreadFile(portable, Unreached: true).Opening("Scoop's portable settings file")}, "
                    + "so Deguffer cannot tell which settings Scoop reads, and leaves its cache alone.";
                return null;

            default:
                return user;
        }
    }

    /// <summary>The value Scoop takes from <paramref name="candidates"/>, as a folder both read alike.</summary>
    private static string? First(string what, out string why, params ScoopValue[] candidates)
    {
        why = string.Empty;

        var chosen = candidates.First(candidate => candidate.IsSet);

        if (chosen.WhyUnfollowed is { } declined)
        {
            why = declined;
            return null;
        }

        var folder = ScoopPath.Followed(chosen.Text!);

        if (folder is null)
        {
            why = Unfollowed($"{what} is set to '{chosen.Text}'");
        }

        return folder;
    }

    private static ScoopValue Variable(IUserEnvironment environment, string name) =>
        ScoopValue.From(environment.GetEnvironmentVariable(name));

    private static string Unfollowed(string subject) =>
        $"{subject}, which Deguffer cannot be sure Scoop reads as the same folder: it is not a full path, or "
        + "it holds a character PowerShell reads as a wildcard. So Scoop's cache is left alone rather "
        + "than one folder examined and another emptied.";
}
