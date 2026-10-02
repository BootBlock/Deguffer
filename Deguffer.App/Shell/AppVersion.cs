using System.Reflection;

namespace Deguffer.App.Shell;

/// <summary>Deguffer's own version, read once, for the About page and for a run's diagnostic report.</summary>
internal static class AppVersion
{
    /// <summary>
    /// The informational version carries a <c>+sha</c> suffix from the build; the commit is not
    /// what someone reading an about box wants, so only the version itself is shown.
    /// </summary>
    public static string Current { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]
        ?? string.Empty;
}
