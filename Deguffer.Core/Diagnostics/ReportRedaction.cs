using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Diagnostics;

/// <summary>
/// Takes the account's and the machine's names out of text that is meant to be posted in public.
///
/// <para><b>Driven by the names this machine knows, not by where a path seems to end.</b> A report
/// holds paths in backticks, in step descriptions and inside messages a tool wrote, followed by
/// anything at all, and Windows writes accounts as <c>DOMAIN\user</c> outside any path. A rule that
/// guesses where a name ends from what surrounds it either leaves a surname behind or erases the
/// sentence beside it. So the profile folder's name, the account, the domain, the machine and each
/// work OneDrive's organisation are matched as whole words wherever they appear, and the paths that
/// hold them are matched as they are known.</para>
///
/// <para><b>Patterns remain only for names nothing here can know</b>: another account's profile under
/// a <c>Users</c> folder, this one's in its short 8.3 spelling, and a work OneDrive this account does
/// not sync. Each ends at a separator, a closing quote or the end of a line, so a surname is never
/// left behind. What that costs is the rest of a sentence that names a bare profile folder with no
/// separator after it, which is rare and loses only words.</para>
///
/// <para><b>What one rule writes, no later rule reads.</b> Each replacement goes in as a marker no
/// name can match and becomes its token at the end, so an account called <c>user</c> cannot turn
/// <c>&lt;user&gt;</c> into <c>&lt;&lt;user&gt;&gt;</c>.</para>
///
/// <para><b>What it cannot see</b> is a name the account gave a folder of its own, such as a project
/// named after a client. The report asks the reader to look before they post for that reason.</para>
/// </summary>
internal sealed partial class ReportRedaction
{
    /// <summary>What may not come either side of a known path: anything that would continue a folder's name.</summary>
    private const string PathChar = @"[\p{L}\p{N}_-]";

    /// <summary>What may not come either side of a known name: a letter or a digit.</summary>
    private const string NameChar = @"[\p{L}\p{N}]";

    /// <summary>
    /// An unknown folder name, up to a separator, a backtick, a double quote, a closing single quote,
    /// a marker or the end of a line. An apostrophe inside a name, as in O'Brien, does not end it.
    /// </summary>
    private const string UnknownName = @"(?:[^\\/`'""\r\n\uE000]|'(?=\p{L}))+";

    private const string OneDrivePrefix = "OneDrive - ";

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly string[] Tokens =
        ["%USERPROFILE%", "<personal folder>", "<organisation>", "<user>", "<machine>", "<domain>"];

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
            .Select(folder => (Literal(folder), Marker("<personal folder>"))));

        if (profile.Length > 0)
        {
            rules.Add((Literal(profile), Marker("%USERPROFILE%")));
        }

        // Before any name is replaced, so a name that is also a folder's cannot take away the
        // "Users" the pattern is anchored on.
        rules.Add((OtherProfile(), "${root}" + Marker("<user>")));

        foreach (var organisation in Organisations(environment.PersonalFolders))
        {
            rules.Add((Word(organisation), Marker("<organisation>")));
        }

        rules.Add((UnknownOrganisation(), "${lead}" + Marker("<organisation>")));

        // A local account's domain is the machine's own name, which the rule after this one covers.
        if (environment.DomainName.Length > 0
            && !environment.DomainName.Equals(environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            rules.Add((Word(environment.DomainName), Marker("<domain>")));
        }

        if (environment.MachineName.Length > 0)
        {
            // With its domain, which a share named by the machine's full name carries.
            rules.Add((Word(environment.MachineName, @"(?:\.[\p{L}\p{N}-]+)*"), Marker("<machine>")));
        }

        // The profile folder's own name as well as the account's: the two differ on an account renamed
        // after it was made, and either is the person's name.
        foreach (var name in new[] { Path.GetFileName(profile), environment.UserName }
                     .Where(name => !string.IsNullOrEmpty(name))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(name => name.Length))
        {
            rules.Add((Word(name), Marker("<user>")));
        }

        _rules = rules;
    }

    public string Apply(string text)
    {
        var marked = _rules.Aggregate(text, (current, rule) => rule.Pattern.Replace(current, rule.Replacement));

        return Tokens.Aggregate(marked, (current, token) => current.Replace(Marker(token), token, StringComparison.Ordinal));
    }

    /// <summary>
    /// <paramref name="token"/> as a marker no rule can match: private-use characters, none of them
    /// a letter, a digit or a separator.
    /// </summary>
    private static string Marker(string token) => $"\uE000{(char)(0xE100 + Array.IndexOf(Tokens, token))}\uE001";

    /// <summary>
    /// The organisations this account's work OneDrive folders are named after, as in
    /// <c>OneDrive - Contoso</c>, found in any part of a personal folder's path: Documents moved into
    /// a work OneDrive is a folder named Documents. OneDrive names each SharePoint library it syncs
    /// after the same organisation, so the name is matched wherever it appears. <c>OneDrive -
    /// Personal</c> is the consumer one beside a work account, and names no organisation.
    /// </summary>
    private static IEnumerable<string> Organisations(IReadOnlyList<string> personalFolders) =>
        personalFolders
            .SelectMany(folder => folder.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            .Where(segment => segment.StartsWith(OneDrivePrefix, StringComparison.OrdinalIgnoreCase))
            .Select(segment => segment[OneDrivePrefix.Length..].Trim())
            .Where(name => name.Length > 0 && !name.Equals("Personal", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(name => name.Length);

    /// <summary>
    /// <paramref name="path"/> in either separator, as tools print both, and only where the path ends
    /// at the end of a name: <c>C:\Users\bob</c> is not matched inside <c>C:\Users\bobby</c> or
    /// <c>C:\Users\bob.CONTOSO</c>, which are other accounts' profiles.
    /// </summary>
    private static Regex Literal(string path) =>
        new(
            $"(?<!{PathChar})"
            + string.Join(@"[\\/]+", path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape))
            + $@"(?!{PathChar}|[.']{NameChar})",
            Options);

    /// <summary>
    /// <paramref name="name"/> as a whole word, followed by <paramref name="suffix"/> where there is
    /// one. A hyphen or an underscore does not join words here, so <c>bob-cache</c> loses the name.
    /// </summary>
    private static Regex Word(string name, string suffix = "") =>
        new($"(?<!{NameChar}){Regex.Escape(name)}{suffix}(?!{NameChar})", Options);

    /// <summary>
    /// A profile folder under a <c>Users</c> folder that no rule above named: another account's, or
    /// this one's in its short 8.3 spelling. Anchored at a drive, in each form a tool may print one
    /// (a letter, an administrative share, WSL's mount and a device path), so a source folder named
    /// <c>Users</c> is not read as one. The profiles Windows itself names are no one's, and saying
    /// which one a path was in is part of the diagnosis.
    /// </summary>
    [GeneratedRegex(
        @"(?<root>(?:[A-Za-z]:|[A-Za-z]\$|/mnt/[A-Za-z]|\\Device\\HarddiskVolume\d+)[\\/]+Users[\\/]+)"
            + @"(?!(?:Public|Default|Default User|All Users)(?!" + PathChar + @")|\uE000)"
            + UnknownName,
        Options)]
    private static partial Regex OtherProfile();

    /// <summary>The organisation of a work OneDrive this account does not sync, which no name above holds.</summary>
    [GeneratedRegex(
        @"(?<lead>OneDrive - )(?!\uE000|Personal(?!" + NameChar + "))" + UnknownName,
        Options)]
    private static partial Regex UnknownOrganisation();
}
