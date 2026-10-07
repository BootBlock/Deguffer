using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// The Windows directory, as the one declaration any provider reaching into it must use.
///
/// <para>Three providers delete paths under <c>C:\Windows</c> through <see cref="Holding"/>, and
/// three more act there by Windows' own command: the driver store, Delivery Optimization and the
/// component store. What they must all agree on is not a shape but a fact: the directory is never
/// enumerated and never a target, its removals need administrator rights, and §9's exclusions are
/// asserted as survivors rather than merely left unmentioned. Written out per provider, one copy
/// would eventually be the one missing <c>WinSxS</c> — and the omission would look exactly like the
/// others, because §9 is enforced by nothing except not naming those paths as targets.</para>
///
/// <para>Only the constants move here. Each provider still writes its own locations inline, so
/// "which paths may this tool delete?" is still answered by reading that provider's own table.</para>
/// </summary>
public static class WindowsSystemRoot
{
    /// <summary>
    /// §9's exclusions, which every rule reaching into this directory has to be shown not to reach.
    ///
    /// Asserting them is what turns "we did not target those" into evidence. §5.6 exists because an
    /// over-broad rule passes every positive assertion, and these are the two paths on the machine
    /// where being over-broad is unrecoverable: a broken uninstall and an unbootable rollback.
    /// </summary>
    public static readonly IReadOnlyList<(string RelativePath, string Reason)> Exclusions =
    [
        ("WinSxS",
            "The Windows component store. §9 excludes it from every removal by path — it is never "
            + "safe to delete by hand, and only DISM's own cleanup may touch it."),
        ("Installer",
            "Cached installer packages. §9 excludes them — removing the wrong one breaks repair and "
            + "uninstall permanently."),
    ];

    /// <summary>
    /// §9's exclusions as full paths, for a provider that acts under the Windows directory by command
    /// rather than through <see cref="Holding"/>. A command is not a rule Deguffer wrote, so it is the
    /// more reason to show afterwards that it left them standing.
    /// </summary>
    public static IEnumerable<(string Path, string Reason)> ExclusionsUnder(string windowsDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(windowsDirectory);

        return Exclusions.Select(e => (Path.Combine(windowsDirectory, e.RelativePath), e.Reason));
    }

    /// <summary>The Windows directory declared with <paramref name="locations"/> under it.</summary>
    public static DeclaredRoot Holding(ISystemDirectories system, params DeclaredLocation[] locations)
    {
        ArgumentNullException.ThrowIfNull(system);

        return new DeclaredRoot(
            system.WindowsDirectory,
            "The Windows directory itself must survive — it is never listed and never a target, and "
            + "only the paths named inside it are removed.",
            RequiresElevation: true,
            locations,
            Exclusions);
    }
}
