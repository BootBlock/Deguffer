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
/// <c>%TEMP%</c> pointing at a drive root, at somebody's Documents folder, or at
/// <c>C:\Windows\System32</c> would turn a clean into a catastrophe, and the variable is exactly the
/// kind of thing §5.2 refuses to trust. So the rule is the one §5.2 states: a candidate Deguffer
/// does not <em>recognise</em> as a temporary folder is declined, with the reason on screen, rather
/// than accepted because nothing on a list of forbidden paths happened to match it. See
/// <see cref="Refuse"/> for the four tests and what each of them catches.</para>
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

        // Seeded with the machine's folder, which is declared below whatever the account's settings
        // say. A %TEMP% pointing into C:\Windows\Temp is then declined as a user root rather than
        // offered twice — and offered the second time without the administrator rights the real
        // declaration carries.
        var accepted = new List<string> { Path.Combine(system.WindowsDirectory, FolderName) };

        foreach (var candidate in Candidates(environment))
        {
            var trimmed = Path.TrimEndingDirectorySeparator(candidate);

            // An exact repeat is the ordinary case rather than a misconfiguration — all four
            // settings name one folder on most machines — so it is skipped in silence rather than
            // reported as something Deguffer declined.
            if (accepted.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Refuse(trimmed, environment, system, accepted) is { } reason)
            {
                refused.Add((trimmed, reason));
                continue;
            }

            accepted.Add(trimmed);

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
    /// The folder names Windows and everything else use for scratch. A candidate carrying one of
    /// them, at itself or at its immediate parent, is a temporary folder; anything else is not
    /// recognised as one.
    /// </summary>
    private static readonly HashSet<string> TemporaryNames =
        new(["Temp", "Tmp"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Why this candidate is not a temporary folder Deguffer will empty, or null where it is one.
    ///
    /// <para>Every test fails closed, and none of them can be satisfied by the default location —
    /// they are all about a redirection. The variable is exactly the kind of thing §5.2 refuses to
    /// trust, because anything on the machine may have written it, and the whole of this provider's
    /// reach follows from whatever it says.</para>
    ///
    /// <list type="number">
    /// <item><b>A drive or share root</b> has no parent to hold it, and emptying one would take
    /// everything on the volume.</item>
    /// <item><b>A candidate that <em>contains</em> a directory the machine is built out of</b> —
    /// the profile, the Windows directory, either program directory, or the machine-wide
    /// application data. Emptying it would take that with it.</item>
    /// <item><b>A candidate that is not recognisably a temporary folder.</b> This is §5.2's
    /// "unrecognised is Tier 4" applied to the root rather than to a child, and it is the rule that
    /// does the work: the containment test above accepts <c>C:\Users\&lt;user&gt;\Documents</c> and
    /// <c>C:\Windows\System32</c>, because neither of them holds anything structural, and a step
    /// labelled "Temporary files" would then delete every file in them older than the cut-off. What
    /// is recognised is a folder named <c>Temp</c> or <c>Tmp</c>, or one sitting directly inside
    /// one — which covers the default location, a <c>D:\Temp</c> somebody chose, and the numbered
    /// per-session folders a Remote Desktop host hands out. A scratch folder called something else
    /// is declined with the reason on screen, which is the direction §5.2 requires the unknown case
    /// to fail in.</item>
    /// <item><b>A candidate that nests with a folder already accepted.</b> Two roots where one sits
    /// inside the other is the case that destroys a live <c>%TEMP%</c>: the outer step's walk has no
    /// idea the inner folder is a target of its own, so it empties it and then removes it, and the
    /// folder Windows will not put back is gone rather than cleared. A Remote Desktop session host
    /// produces exactly that pairing by default — <c>%TEMP%</c> is <c>…\Local\Temp\2</c> while the
    /// default location is <c>…\Local\Temp</c> — so it is a configuration in the field rather than a
    /// hypothetical. The first accepted wins, and <see cref="Candidates"/> yields the folder this
    /// process would actually use first.</item>
    /// </list>
    /// </summary>
    /// <param name="accepted">
    /// The folders already declared, including the machine's own, which is seeded before any
    /// candidate is considered so that a <c>%TEMP%</c> pointing into <c>C:\Windows\Temp</c> cannot
    /// be declared a second time without the administrator rights the real declaration carries.
    /// </param>
    private static string? Refuse(
        string candidate,
        IUserEnvironment environment,
        ISystemDirectories system,
        IReadOnlyList<string> accepted)
    {
        if (string.IsNullOrEmpty(Path.GetDirectoryName(candidate)))
        {
            return "It is the root of a drive or a share rather than a folder inside one, and "
                + "emptying it would take everything on the volume.";
        }

        string[] mustNotHold =
        [
            environment.UserProfile,
            environment.RoamingAppData,
            environment.LocalAppData,
            system.WindowsDirectory,
            system.ProgramData,
            system.ProgramFiles,
            system.ProgramFilesX86,
        ];

        // Asked before the name test, because it is the more specific answer where both apply: a
        // folder holding the profile is worth saying so about, where "we did not recognise it"
        // would be true and much less use.
        if (mustNotHold.Any(inside => inside.Length > 0 && LongPath.Contains(candidate, inside)))
        {
            return "It holds a directory Windows is built out of, so emptying it would take far "
                + "more than temporary files.";
        }

        if (!IsNamedAsTemporary(candidate))
        {
            return "Nothing about it says it is a temporary folder — neither it nor any folder "
                + "above it is called Temp or Tmp — so Deguffer will not empty it on the strength "
                + "of a setting alone.";
        }

        // Either direction, because either one is the same accident: one walk reaches the other
        // folder, and the folder it reaches is one that has to survive.
        return accepted.Any(other =>
            LongPath.Contains(candidate, other) || LongPath.Contains(other, candidate))
            ? "Another temporary folder sits inside it, or it sits inside one, and emptying the "
                + "outer folder would delete the inner one rather than clear it."
            : null;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is called <c>Temp</c> or <c>Tmp</c>, or sits directly
    /// inside a folder that is.
    ///
    /// <para><b>One level, and no further.</b> The parent is looked at because a per-session
    /// temporary folder is a number inside one — <c>…\Local\Temp\2</c> — and that is the shape a
    /// Remote Desktop host hands out by default. Walking the whole ancestry instead would accept
    /// anything at any depth below a folder that happens to be called <c>Temp</c>, which is most of
    /// what a developer's scratch tree contains and is not what Windows means by a temporary
    /// folder.</para>
    ///
    /// <para>So <c>D:\Temp\build\scratch</c> is declined. That is the conservative direction and it
    /// costs a sentence on screen, where the other reading costs somebody the contents of a folder
    /// nobody meant to name.</para>
    /// </summary>
    private static bool IsNamedAsTemporary(string candidate) =>
        TemporaryNames.Contains(Path.GetFileName(candidate))
        || (Path.GetDirectoryName(candidate) is { } parent
            && TemporaryNames.Contains(Path.GetFileName(parent)));

    /// <summary>The note the user is shown for a candidate that was refused, or null if none was.</summary>
    public static PlanNote? NoteFor(IReadOnlyList<(string Path, string Reason)> refused)
    {
        ArgumentNullException.ThrowIfNull(refused);

        if (refused.Count == 0)
        {
            return null;
        }

        // Each declined folder with its own reason, on its own line. They are declined for
        // different reasons, and one reason attached to a list of folders would be wrong about all
        // but the first — which is the sentence the user would act on.
        var declined = refused.Select(r => $"'{LongPath.Display(r.Path)}' — {r.Reason}");

        // A warning rather than information: the machine is configured in a way that stops Deguffer
        // doing what the row says it does, and only the user can change it.
        return new PlanNote(
            PlanNoteSeverity.Warning,
            (refused.Count == 1
                ? "This machine points a temporary-folder setting at a folder Deguffer will not empty: "
                : "This machine points temporary-folder settings at folders Deguffer will not empty: ")
            + string.Join(" ", declined));
    }
}
