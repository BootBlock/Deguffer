using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Diagnostics;

/// <summary>
/// Takes the account's and the machine's names out of text that is meant to be posted in public.
///
/// <para><b>Driven by the names this machine knows, not by where a path seems to end.</b> A report
/// holds paths in backticks, in step descriptions and inside messages a tool wrote, followed by
/// anything at all, and Windows writes accounts as <c>MACHINE\user</c> outside any path. A rule that
/// guesses where a name ends from what surrounds it either leaves a surname behind or erases the
/// sentence beside it. So the profile folder's name, the account, the machine and each work
/// OneDrive's organisation are matched as whole words wherever they appear, and the paths that hold
/// them are matched as they are known.</para>
///
/// <para><b>Patterns remain only for names nothing here can know</b>: another account's profile
/// under a drive's <c>Users</c>, this one's in its short 8.3 spelling, and a work OneDrive this
/// account does not sync. Each ends at a separator, a quote or the end of a line, so a surname is
/// never left behind. What that costs is the rest of a sentence that names a bare profile folder
/// with no separator after it, which is rare and loses only words.</para>
///
/// <para><b>What it cannot see</b> is a name the account gave a folder of its own, such as a
/// project named after a client. The report asks the reader to look before they post for that
/// reason.</para>
/// </summary>
internal sealed partial class ReportRedaction
{
    /// <summary>Where a word may not continue: a letter, a digit, an underscore or a hyphen.</summary>
    private const string WordChar = @"[\p{L}\p{N}_-]";

    /// <summary>An unknown folder name, up to the first separator, backtick, quote or line end.</summary>
    private const string UnknownName = @"[^\\/`'""\r\n]+";

    private const string OrganisationToken = "<organisation>";

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private readonly IReadOnlyList<(Regex Pattern, string Replacement)> _rules;

    public ReportRedaction(IUserEnvironment environment)
    {
        var profile = Path.TrimEndingDirectorySeparator(environment.UserProfile);
        var rules = new List<(Regex, string)>();

        // A personal folder Windows or OneDrive moved out of the profile, longest first so one inside
        // another goes first. A drive's root, or a folder holding the profile, names no one's files
        // and would swallow every path on the drive.
        rules.AddRange(environment.PersonalFolders
            .Select(Path.TrimEndingDirectorySeparator)
            .Where(folder => folder.Length > 0
                && !string.Equals(Path.GetPathRoot(folder), folder, StringComparison.OrdinalIgnoreCase)
                && !LongPath.Contains(folder, profile)
                && !LongPath.Contains(profile, folder))
            .OrderByDescending(folder => folder.Length)
            .Select(folder => (Literal(folder), "<personal folder>")));

        if (profile.Length > 0)
        {
            rules.Add((Literal(profile), "%USERPROFILE%"));
        }

        foreach (var organisation in Organisations(environment.PersonalFolders))
        {
            rules.Add((Word(organisation), OrganisationToken));
        }

        rules.Add((UnknownOrganisation(), "${lead}" + OrganisationToken));
        rules.Add((OtherProfile(), "${root}<user>"));

        if (environment.MachineName.Length > 0)
        {
            // With its domain, which a share named by the machine's full name carries.
            rules.Add((Word(environment.MachineName, @"(?:\.[\p{L}\p{N}-]+)*"), "<machine>"));
        }

        // The profile folder's own name as well as the account's: the two differ on an account renamed
        // after it was made, and either is the person's name.
        foreach (var name in new[] { Path.GetFileName(profile), environment.UserName }
                     .Where(name => !string.IsNullOrEmpty(name))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(name => name.Length))
        {
            rules.Add((Word(name), "<user>"));
        }

        _rules = rules;
    }

    public string Apply(string text) =>
        _rules.Aggregate(text, (current, rule) => rule.Pattern.Replace(current, rule.Replacement));

    /// <summary>
    /// The organisations this account's work OneDrive folders are named after, as in
    /// <c>OneDrive - Contoso</c>. OneDrive names each SharePoint library it syncs after the same
    /// organisation, so the name is matched wherever it appears rather than only after the prefix.
    /// </summary>
    private static IEnumerable<string> Organisations(IReadOnlyList<string> personalFolders) =>
        personalFolders
            .Select(folder => Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)))
            .Where(name => name.StartsWith("OneDrive - ", StringComparison.OrdinalIgnoreCase))
            .Select(name => name["OneDrive - ".Length..].Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(name => name.Length);

    /// <summary>
    /// <paramref name="path"/> in either separator, as tools print both, and only where the path ends
    /// at the end of a name: <c>C:\Users\bob</c> is not matched inside <c>C:\Users\bobby</c>.
    /// </summary>
    private static Regex Literal(string path) =>
        new(
            $"(?<!{WordChar})"
            + string.Join(@"[\\/]+", path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape))
            + $"(?!{WordChar})",
            Options);

    /// <summary><paramref name="name"/> as a whole word, followed by <paramref name="suffix"/> where there is one.</summary>
    private static Regex Word(string name, string suffix = "") =>
        new($"(?<!{WordChar}){Regex.Escape(name)}{suffix}(?!{WordChar})", Options);

    /// <summary>
    /// A profile folder under a drive's <c>Users</c> that no rule above named: another account's, or
    /// this one's in its short 8.3 spelling. Anchored at the drive, so a source folder named
    /// <c>Users</c> is not read as one. The profiles Windows itself names are no one's, and saying
    /// which one a path was in is part of the diagnosis.
    /// </summary>
    [GeneratedRegex(
        @"(?<root>[A-Za-z]:[\\/]+Users[\\/]+)(?!(?:<user>|%USERPROFILE%|Public|Default|Default User|All Users)(?!" + WordChar + "))"
            + UnknownName,
        Options)]
    private static partial Regex OtherProfile();

    /// <summary>The organisation of a work OneDrive this account does not sync, which none of the names above holds.</summary>
    [GeneratedRegex(@"(?<lead>OneDrive - )(?!" + OrganisationToken + ")" + UnknownName, Options)]
    private static partial Regex UnknownOrganisation();
}
