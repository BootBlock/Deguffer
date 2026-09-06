using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <param name="Roots">The scratch folders this machine actually has, ready to be examined.</param>
/// <param name="Refused">
/// Paths that named themselves as a temporary folder and were declined, with the reason. Reported
/// rather than dropped, because a redirected <c>%TEMP%</c> is the one case where the user can see a
/// folder Deguffer will not touch and has no way to find out why.
/// </param>
public sealed record TempRootSet(
    IReadOnlyList<DeclaredRoot> Roots,
    IReadOnlyList<(string Path, string Reason)> Refused);

/// <summary>
/// Where this machine's temporary folders are, and which of them Deguffer will reach into.
///
/// <para><b>There is more than one, and the number is not fixed.</b> Windows gives each account a
/// scratch folder and the machine another, and the account's is redirectable through two
/// environment variables that are allowed to disagree with each other and with the default
/// location. <c>Path.GetTempPath</c> answers with the one <em>this process</em> would use, which is
/// the first of <c>%TMP%</c> and <c>%TEMP%</c> that is set — so a machine where the two point at
/// different folders has one that nothing would ever look at. All four candidates are resolved and
/// deduplicated instead.</para>
///
/// <para><b>§5.2 applies to the root itself here, which is unusual.</b> Every other provider knows
/// where its cache is; this one is told, by an environment variable anything may have written. A
/// <c>%TEMP%</c> pointing at a drive root, at the profile, or at the Windows directory would turn a
/// clean into a catastrophe, and the variable is exactly the kind of thing §5.2 refuses to trust.
/// So a candidate is checked before it is declared, and one that fails is refused with a reason
/// rather than quietly skipped.</para>
///
/// <para><b><c>C:\Windows\SystemTemp</c> is deliberately not among them.</b> Windows 11 writes
/// servicing work there rather than in <c>C:\Windows\Temp</c>, and §9 keeps Deguffer out of
/// servicing internals. It is also not a folder anything documents as safe to empty, which on a
/// directory whose failure mode is a broken update is the end of the argument.</para>
/// </summary>
public static class TempRoots
{
    /// <summary>
    /// What the account's own scratch folder is called under <c>%LOCALAPPDATA%</c>, and what
    /// Windows itself calls the machine's, inside the Windows directory. The same word both times,
    /// and named once so it stays that way.
    /// </summary>
    private const string FolderName = "Temp";

    private const string UserReason =
        "Scratch files this account's programs left behind. Installers, compilers, browsers and "
        + "test runners unpack here and are meant to clear up afterwards.";

    private const string MachineReason =
        "Scratch files Windows and the services running under system accounts left behind, in the "
        + "temporary folder they share rather than in any one profile.";

    /// <summary>
    /// The scratch folders on this machine, in the order they are offered: the account's own first,
    /// then the machine's.
    /// </summary>
    public static TempRootSet Resolve(IUserEnvironment environment, ISystemDirectories system)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(system);

        var roots = new List<DeclaredRoot>();
        var refused = new List<(string Path, string Reason)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in Candidates(environment))
        {
            var trimmed = Path.TrimEndingDirectorySeparator(candidate);

            if (!seen.Add(trimmed))
            {
                continue;
            }

            if (Refuse(trimmed, environment, system) is { } reason)
            {
                refused.Add((trimmed, reason));
                continue;
            }

            // Declared as its parent holding one named child, which is what every declared location
            // is. The parent then goes into §5.6's report as a survivor, so a run reaching into a
            // redirected temporary folder produces evidence that it stayed inside it.
            roots.Add(new DeclaredRoot(
                Path.GetDirectoryName(trimmed)!,
                $"The folder holding {Path.GetFileName(trimmed)} must survive — only what is inside "
                + "the temporary folder itself is removed.",
                RequiresElevation: false,
                [new DeclaredLocation(Path.GetFileName(trimmed), UserReason, DeclaredLocationKind.DirectoryContents)],
                []));
        }

        roots.Add(WindowsSystemRoot.Holding(
            system,
            new DeclaredLocation(FolderName, MachineReason, DeclaredLocationKind.DirectoryContents)));

        return new TempRootSet(roots, refused);
    }

    /// <summary>
    /// Every path that claims to be this account's temporary folder, before any of them is checked.
    ///
    /// <para><see cref="IUserEnvironment.TempPath"/> is what this process would use and is almost
    /// always the answer. The two variables are read as well because they are allowed to disagree
    /// with it and with each other, and the default location is read because a machine where both
    /// are set elsewhere still has one — programs that resolve the folder for themselves rather
    /// than from the environment write to it, and nothing else would ever look there.</para>
    /// </summary>
    private static IEnumerable<string> Candidates(IUserEnvironment environment)
    {
        yield return environment.TempPath;

        if (LongPath.Configured(environment.GetEnvironmentVariable("TMP")) is { } tmp)
        {
            yield return tmp;
        }

        if (LongPath.Configured(environment.GetEnvironmentVariable("TEMP")) is { } temp)
        {
            yield return temp;
        }

        yield return Path.Combine(environment.LocalAppData, FolderName);
    }

    /// <summary>
    /// Why this candidate is not a temporary folder Deguffer will empty, or null where it is one.
    ///
    /// <para>Both tests fail closed, and both are about a redirection rather than about the default
    /// location, which can satisfy neither condition. A drive or share root has no parent to hold
    /// it, and emptying one would take everything on the volume. A candidate that <em>contains</em>
    /// a directory the machine is built out of would take that with it — the profile, the Windows
    /// directory, either program directory, or the machine-wide application data.</para>
    ///
    /// <para>The second test is written as containment rather than as a list of forbidden paths, so
    /// a variable pointing at <c>C:\</c>'s child, or at the profile's parent, is caught by the same
    /// rule as one pointing at the profile itself.</para>
    /// </summary>
    private static string? Refuse(string candidate, IUserEnvironment environment, ISystemDirectories system)
    {
        if (string.IsNullOrEmpty(Path.GetDirectoryName(candidate)))
        {
            return "It is the root of a drive or a share rather than a folder inside one, and "
                + "emptying it would take everything on the volume.";
        }

        string[] mustNotBeInside =
        [
            environment.UserProfile,
            environment.RoamingAppData,
            environment.LocalAppData,
            system.WindowsDirectory,
            system.ProgramData,
            system.ProgramFiles,
            system.ProgramFilesX86,
        ];

        return mustNotBeInside.Any(inside => inside.Length > 0 && LongPath.Contains(candidate, inside))
            ? "It holds a directory Windows is built out of, so emptying it would take far more "
                + "than temporary files."
            : null;
    }

    /// <summary>The note the user is shown for a candidate that was refused, or null if none was.</summary>
    public static PlanNote? NoteFor(IReadOnlyList<(string Path, string Reason)> refused)
    {
        ArgumentNullException.ThrowIfNull(refused);

        if (refused.Count == 0)
        {
            return null;
        }

        var declined = refused.Select(r => $"'{LongPath.Display(r.Path)}' ({r.Reason})");

        // A warning rather than information: the machine is configured in a way that stops Deguffer
        // doing what the row says it does, and only the user can change it.
        return new PlanNote(
            PlanNoteSeverity.Warning,
            $"This machine points a temporary-folder setting at {string.Join(", ", declined)} "
            + "Deguffer will not empty it.");
    }
}
