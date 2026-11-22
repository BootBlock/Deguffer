using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// The configuration files pip reads from fixed places on Windows: the site-wide
/// <c>%ProgramData%\pip\pip.ini</c>, the legacy per-user <c>%USERPROFILE%\pip\pip.ini</c> and the
/// per-user <c>%APPDATA%\pip\pip.ini</c>, in the order pip reads them
/// (https://pip.pypa.io/en/stable/topics/configuration/, and <c>get_configuration_files</c> in pip's
/// <c>configuration.py</c>). pip does not read <c>%LOCALAPPDATA%\pip\pip.ini</c>.
///
/// <para>The interpreter's <c>&lt;sys.prefix&gt;\pip.ini</c> and the file <c>PIP_CONFIG_FILE</c>
/// names are not here: each depends on the pip asked, so only <see cref="PipLocationInputs"/> can
/// name them.</para>
/// </summary>
internal static class PipConfigurationFiles
{
    /// <summary>pip's fixed configuration files, in the order pip reads them.</summary>
    /// <param name="environment">The profile the per-user files are in.</param>
    /// <param name="system">Where the site-wide file is.</param>
    public static IReadOnlyList<PipConfigurationFile> Of(IUserEnvironment environment, ISystemDirectories system) =>
    [
        new(Path.Combine(system.ProgramData, "pip", "pip.ini"), "site-wide"),
        new(Path.Combine(environment.UserProfile, "pip", "pip.ini"), "legacy per-user"),
        new(Path.Combine(environment.RoamingAppData, "pip", "pip.ini"), "roaming per-user"),
    ];
}

/// <summary>One configuration file pip reads.</summary>
/// <param name="Path">Where the file is.</param>
/// <param name="Scope">Which of pip's files it is, worded to follow "your" or "the" in a sentence.</param>
internal sealed record PipConfigurationFile(string Path, string Scope);
