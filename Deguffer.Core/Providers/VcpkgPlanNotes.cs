using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What a vcpkg plan tells the user about the clone and the variables it could not use.
/// </summary>
internal static class VcpkgPlanNotes
{
    /// <summary>
    /// What the user is told when a variable named vcpkg's own directory, or something in it, rather
    /// than a cache.
    /// </summary>
    public static string Refused(string path) =>
        $"Leaving '{path}' alone: that is vcpkg's own directory, or something inside it Deguffer "
        + "never removes, rather than a cache. A variable can say where a cache is. It cannot ask "
        + "for the directory holding the tool.";

    /// <summary>The warnings for the clone and the integration file, where either could not be read.</summary>
    public static IEnumerable<PlanNote> Unreached(VcpkgLocations located)
    {
        if (located.UnreachedRoot is { } unreached)
        {
            yield return UnreachedRoot(unreached);
        }

        if (located.UnreadIntegrationFile is { } file)
        {
            yield return UnreadIntegrationFile(file);
        }
    }

    /// <summary>What the user is told when the integration file could not be read.</summary>
    private static PlanNote UnreadIntegrationFile(UnreadFile file) => new(
        PlanNoteSeverity.Warning,
        file.Opening("vcpkg's record of the clone it integrated with Visual Studio")
        + ", so that clone may not have been found. The buildtrees, downloads and packages directories "
        + "of a clone that was not found were neither cleared nor ruled out.");

    /// <summary>
    /// What the user is told when something named a clone that Windows would not describe. It does
    /// not say the marker is missing, which nothing established, and it gives no advice to set a
    /// variable, which may be exactly what named it.
    /// </summary>
    private static PlanNote UnreachedRoot(string unreached) => new(
        PlanNoteSeverity.Warning,
        $"Something on this machine names '{unreached}' as vcpkg's own directory, and Windows would not "
        + "say what is there, so Deguffer could not check it or look inside it. A link Windows will not "
        + "follow, a folder this account may not read and a drive that is not connected all do that. The "
        + "buildtrees, downloads and packages directories of a vcpkg clone were neither cleared nor "
        + "ruled out.");

    /// <summary>
    /// What the user is told when something named a clone and Deguffer declined it. It names the
    /// directory and the marker, because "could not find it" would be untrue and the advice that
    /// sentence goes on to give has already been taken.
    /// </summary>
    public static string UnmarkedRoot(string declined) =>
        $"Deguffer found {declined} but it does not hold vcpkg's own '{VcpkgDiscovery.RootMarker}' "
        + "marker, so it did not look inside it. The buildtrees, downloads and packages directories of "
        + "a vcpkg clone are not covered here.";
}
