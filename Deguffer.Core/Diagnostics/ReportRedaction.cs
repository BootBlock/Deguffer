using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Diagnostics;

/// <summary>
/// Takes the account's and the machine's names out of text that is meant to be posted in public.
///
/// <para><b>Each rule says where a name ends, and that is the hard half.</b> A report holds paths in
/// backticks, in step descriptions and inside messages a tool wrote, so a path may be followed by a
/// separator, a quote, a full stop, a space or the end of a line. A rule that ends a name too early
/// leaves the rest of it in the report, and one that ends it too late erases the diagnosis beside
/// it. Where the two conflict, a profile folder's name is ended late, because a surname left in a
/// public issue cannot be taken back and a line lost from a report can be asked for.</para>
///
/// <para><b>What it cannot see</b> is a name the account gave a folder of its own, such as a
/// project named after a client. The report asks the reader to look before they post for that
/// reason.</para>
/// </summary>
internal sealed partial class ReportRedaction
{
    /// <summary>
    /// Where a folder name that may hold spaces ends: a separator, a backtick, a quote, the
    /// " — " a report puts between a step and its message, or the end of a line.
    /// </summary>
    private const string FolderEnd = @"(?=[\\/`'""]|\s—|[\r\n]|$)";

    /// <summary>A folder name that may hold spaces, taken as short as <see cref="FolderEnd"/> allows.</summary>
    private const string FolderName = @"[^\\/`'""\r\n]+?";

    /// <summary>Where a known name ends: anything that cannot continue a folder name.</summary>
    private const string NameEnd = @"(?=[\\/`'""\s.,:;)\]]|$)";

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private readonly IReadOnlyList<(Regex Pattern, string Replacement)> _rules;

    public ReportRedaction(IUserEnvironment environment)
    {
        var profile = Path.TrimEndingDirectorySeparator(environment.UserProfile);

        // Longest first, so a folder inside another is replaced before the one holding it. A personal
        // folder inside the profile needs no rule of its own: the profile's covers it, and keeps the
        // part of its path that says which folder it was.
        var moved = environment.PersonalFolders
            .Select(Path.TrimEndingDirectorySeparator)
            .Where(folder => folder.Length > 0 && !LongPath.Contains(profile, folder))
            .OrderByDescending(folder => folder.Length);

        var rules = new List<(Regex, string)>();

        rules.AddRange(moved.Select(folder => (Literal(folder), "<personal folder>")));

        if (profile.Length > 0)
        {
            rules.Add((Literal(profile), "%USERPROFILE%"));
        }

        rules.Add((Organisation(), "${lead}<organisation>"));
        rules.Add((OtherProfile(), "${root}<user>"));

        if (environment.MachineName.Length > 0)
        {
            // A share named by the machine's full name carries its domain as well.
            rules.Add((new Regex($@"(?<=\\\\){Regex.Escape(environment.MachineName)}(?:\.[^\\/\s`'""]+)?", Options), "<machine>"));
            rules.Add((Segment(environment.MachineName), "<machine>"));
        }

        if (environment.UserName.Length > 0)
        {
            rules.Add((Segment(environment.UserName), "<user>"));
        }

        _rules = rules;
    }

    public string Apply(string text) =>
        _rules.Aggregate(text, (current, rule) => rule.Pattern.Replace(current, rule.Replacement));

    /// <summary>
    /// <paramref name="path"/> in either separator, as tools print both, and ended where a folder name
    /// ends, so a profile <c>C:\Users\bob</c> does not match inside <c>C:\Users\bobby</c>.
    /// </summary>
    private static Regex Literal(string path) =>
        new(
            string.Join(@"[\\/]+", path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape))
            + FolderEnd,
            Options);

    /// <summary><paramref name="name"/> wherever it is a whole path segment, and nowhere else.</summary>
    private static Regex Segment(string name) => new($@"(?<=[\\/]){Regex.Escape(name)}{NameEnd}", Options);

    /// <summary>
    /// A profile folder under a drive's <c>Users</c>: another account's, or this one's in a spelling
    /// the literal rule did not match, such as its short 8.3 name. Anchored at the drive, so a source
    /// folder named <c>Users</c> is not read as one. The profiles Windows itself names are no one's,
    /// and saying which one a path was in is part of the diagnosis.
    /// </summary>
    [GeneratedRegex(
        @"(?<root>[A-Za-z]:[\\/]+Users[\\/]+)(?!(?:Public|Default|Default User|All Users)" + FolderEnd + ")"
            + FolderName + FolderEnd,
        Options)]
    private static partial Regex OtherProfile();

    /// <summary>The organisation a work OneDrive folder is named after, as in <c>OneDrive - Contoso</c>.</summary>
    [GeneratedRegex(@"(?<lead>[\\/]OneDrive - )" + FolderName + FolderEnd, Options)]
    private static partial Regex Organisation();
}
