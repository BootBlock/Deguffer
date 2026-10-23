using System.Text.RegularExpressions;

namespace Deguffer.Core.Safety;

/// <summary>
/// A command as Windows would run it: the executable it names and what follows. One reading for
/// every command a registry value records, an uninstall command (§7.3) and a startup entry (§7.5)
/// among them, so two pages never disagree about which program one command starts.
/// </summary>
/// <param name="Executable">The executable, as the command names it.</param>
/// <param name="Arguments">Everything after it, trimmed.</param>
/// <param name="Presence">
/// What Windows said about <paramref name="Executable"/>, or null where it is not a fully qualified
/// path and so was not asked about: a bare name or a relative path resolves against a search path
/// this cannot see.
/// </param>
public sealed partial record CommandLine(string Executable, string Arguments, PathPresence? Presence)
{
    /// <summary>
    /// Read <paramref name="text"/> the way Windows would run it, or null where it is empty or is a
    /// quoted command whose quote never closes.
    ///
    /// <para>A quoted command's executable is the quoted span. An unquoted one is the shortest
    /// prefix ending in <c>.exe</c> that is a file, which is how an unquoted path with spaces
    /// resolves. Where no prefix is a file, the answer keeps whether Windows refused any of them,
    /// because only "absent" everywhere proves the program gone.</para>
    /// </summary>
    /// <param name="probeFile">Asks whether a file is there, keeping a refusal apart from absence.</param>
    public static CommandLine? Read(string? text, Func<string, PathPresence> probeFile)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();

        return text[0] == '"' ? Quoted(text, probeFile) : Unquoted(text, probeFile);
    }

    private static CommandLine? Quoted(string text, Func<string, PathPresence> probeFile)
    {
        var close = text.IndexOf('"', 1);

        if (close < 2)
        {
            return null;
        }

        var executable = text[1..close];
        var arguments = text[(close + 1)..].Trim();

        return new CommandLine(executable, arguments, Path.IsPathFullyQualified(executable) ? probeFile(executable) : null);
    }

    private static CommandLine Unquoted(string text, Func<string, PathPresence> probeFile)
    {
        var candidates = new List<int>();

        foreach (Match match in ExecutableEnd().Matches(text))
        {
            candidates.Add(match.Index + match.Length);
        }

        if (candidates.Count == 0)
        {
            var space = text.IndexOf(' ');

            return space < 0
                ? new CommandLine(text, string.Empty, null)
                : new CommandLine(text[..space], text[(space + 1)..].Trim(), null);
        }

        var probes = new List<(string Executable, PathPresence Presence)>(candidates.Count);

        foreach (var end in candidates)
        {
            var executable = text[..end];

            if (!Path.IsPathFullyQualified(executable))
            {
                // A bare name, or a relative path, which resolves against a search path this cannot
                // see. The first candidate decides: nothing after it is a path either.
                return new CommandLine(executable, text[end..].Trim(), null);
            }

            var presence = probeFile(executable);
            probes.Add((executable, presence));

            if (presence is PathPresence.Present)
            {
                break;
            }
        }

        var chosen = probes.FirstOrDefault(p => p.Presence is PathPresence.Present);

        if (chosen.Executable is null)
        {
            chosen = probes.FirstOrDefault(p => p.Presence is PathPresence.Refused);
        }

        if (chosen.Executable is null)
        {
            chosen = probes[0];
        }

        return new CommandLine(chosen.Executable, text[chosen.Executable.Length..].Trim(), chosen.Presence);
    }

    /// <summary>The end of each <c>.exe</c> that is followed by a space or by the end of the command.</summary>
    [GeneratedRegex(@"\.exe(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExecutableEnd();
}
