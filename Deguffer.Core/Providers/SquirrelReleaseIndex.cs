using System.Buffers;
using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>What became of reading an application's <c>packages\RELEASES</c>.</summary>
public enum SquirrelIndexState
{
    /// <summary>
    /// Windows says the file is not there. <c>Update.exe --processStart</c> reads it with an
    /// unguarded <c>File.ReadAllText</c>, so a shortcut of that kind starts nothing at all — before
    /// any build is removed and after.
    /// </summary>
    Absent,

    /// <summary>Windows would not say whether the file is there.</summary>
    Unreached,

    /// <summary>
    /// The file is there and would not be read, or holds a line that is not an index entry. Squirrel
    /// throws on such a line; this cannot tell whether Squirrel's own parser would have, so it is not
    /// an answer either way.
    /// </summary>
    Unreadable,

    /// <summary>Read, and every line was an entry.</summary>
    Read,
}

/// <summary>
/// Squirrel's own record of the packages an application holds, read once per planning pass.
///
/// <para><b>Two questions are asked of it, and both are the updater's own.</b> The packages row asks
/// which package files the index still names, which is what Squirrel's prune keeps. The ordering of
/// builds asks which build <c>Update.exe --processStart</c> starts: it orders the entries by version,
/// newest first, and starts the first whose <c>app-</c> folder exists. The updater rewrites this file
/// only <em>after</em> it has unpacked a new build, so an index that does not lead to the newest
/// folder is an update that stopped part-way.</para>
/// </summary>
/// <param name="State">Whether the file was read.</param>
/// <param name="Names">
/// The file names the index refers to, which is empty unless <paramref name="State"/> is
/// <see cref="SquirrelIndexState.Read"/>. An index naming nothing would make every package in the
/// folder removable, which is exactly what an unread index must not be allowed to mean — so a caller
/// reads the state first.
/// </param>
public sealed partial record SquirrelReleaseIndex(SquirrelIndexState State, IReadOnlySet<string> Names)
{
    /// <summary>The file's name, inside the application's packages folder.</summary>
    public const string FileName = "RELEASES";

    /// <summary>
    /// One line of that index: a SHA-1, the file name, and the size. Squirrel's own parser, with the
    /// same shape and the same strictness — a line that does not match makes Squirrel throw, and it
    /// makes this report the index unreadable.
    /// </summary>
    [GeneratedRegex(@"\A([0-9a-fA-F]{40})\s+(\S+)\s+([0-9]+)\s*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Entry();

    /// <summary>A comment, which Squirrel strips before it parses a line.</summary>
    [GeneratedRegex(@"\s*#.*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Comment();

    /// <summary>
    /// The characters Windows will not accept in a file name, as the set each line is tested
    /// against. <see cref="Path.GetInvalidFileNameChars"/> clones its array on every call so a
    /// caller cannot mutate it, and the check runs once per line (G5).
    /// </summary>
    private static readonly SearchValues<char> InvalidInFileName =
        SearchValues.Create(Path.GetInvalidFileNameChars());

    private static readonly IReadOnlySet<string> Nothing = new HashSet<string>();

    /// <summary>Read the index in <paramref name="packagesDirectory"/>.</summary>
    /// <param name="packagesDirectory">
    /// The application's packages folder. It may be a link, and is read through: the updater reads
    /// through it too, and reading decides nothing about what is removed from it.
    /// </param>
    public static SquirrelReleaseIndex Read(string packagesDirectory)
    {
        var path = Path.Combine(packagesDirectory, FileName);

        switch (LongPath.ProbeFile(path))
        {
            case PathPresence.Absent:
                return new SquirrelReleaseIndex(SquirrelIndexState.Absent, Nothing);

            case PathPresence.Refused:
                return new SquirrelReleaseIndex(SquirrelIndexState.Unreached, Nothing);
        }

        string text;

        try
        {
            // The same encoding Squirrel reads it with, so the byte-order mark a real RELEASES
            // carries is stripped here exactly as it is there.
            text = File.ReadAllText(LongPath.Extended(path), System.Text.Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Held open while the updater rewrites it, refused to this account, or gone since the
            // probe. None of them established what the index says.
            return new SquirrelReleaseIndex(SquirrelIndexState.Unreadable, Nothing);
        }

        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in text.Split('\n'))
        {
            var entry = Comment().Replace(line, string.Empty).Trim();

            if (entry.Length == 0)
            {
                continue;
            }

            if (Entry().Match(entry) is not { Success: true } match)
            {
                return new SquirrelReleaseIndex(SquirrelIndexState.Unreadable, Nothing);
            }

            var name = match.Groups[2].Value;

            // A local index holds bare file names. Squirrel's own parser also accepts an absolute
            // HTTP URL, which belongs to a remote feed rather than to this folder — meeting one
            // here means the file is not what it was taken for.
            if (name.AsSpan().IndexOfAny(InvalidInFileName) >= 0)
            {
                return new SquirrelReleaseIndex(SquirrelIndexState.Unreadable, Nothing);
            }

            named.Add(name);
        }

        return new SquirrelReleaseIndex(SquirrelIndexState.Read, named);
    }

    /// <summary>
    /// What stands between <c>Update.exe --processStart</c> and the newest of
    /// <paramref name="versions"/>, read only where <see cref="State"/> is
    /// <see cref="SquirrelIndexState.Read"/>: nothing where it starts that build.
    ///
    /// <para>Squirrel's rule, followed step for step: the entries newest first, and for each the
    /// folder named for its version and then the one named for its first three components, which the
    /// updater looks for to reach builds an older Squirrel installed. The first that exists is the
    /// build it starts, and where none exists it starts nothing.</para>
    ///
    /// <para>An entry whose version cannot be read leaves the order unknown rather than skipped:
    /// Squirrel orders a pre-release by a rule this does not reproduce.</para>
    /// </summary>
    /// <param name="versions">
    /// The installation's builds, oldest first, no two of them named alike but for case —
    /// <see cref="SquirrelInstallation"/> settles that before it asks.
    /// </param>
    public SquirrelOrderDoubt DoubtAbout(IReadOnlyList<SquirrelVersionDirectory> versions)
    {
        var entries = new List<(Version Number, string Text)>(Names.Count);

        foreach (var name in Names)
        {
            if (!SquirrelPackages.TryReadVersion(name, out var number, out var text))
            {
                return SquirrelOrderDoubt.IndexUnorderable;
            }

            entries.Add((number, text));
        }

        var byName = versions.ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var (number, text) in entries.OrderByDescending(e => e.Number))
        {
            string[] candidates =
            [
                "app-" + text,
                $"app-{number.Major}.{number.Minor}.{Math.Max(number.Build, 0)}",
            ];

            foreach (var candidate in candidates)
            {
                if (byName.TryGetValue(candidate, out var started))
                {
                    return started == versions[^1] ? SquirrelOrderDoubt.None : SquirrelOrderDoubt.IndexBehind;
                }
            }
        }

        return SquirrelOrderDoubt.IndexBehind;
    }
}
