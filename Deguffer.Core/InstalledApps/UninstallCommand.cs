using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.InstalledApps;

/// <summary>
/// What an entry's <c>UninstallString</c> runs, as far as the machine can tell (§7.3).
/// </summary>
public abstract partial record UninstallCommand
{
    /// <summary>
    /// Read <paramref name="text"/> the way Windows would run it.
    ///
    /// <para>A quoted command's executable is the quoted span. An unquoted one is the shortest
    /// prefix ending in <c>.exe</c> that is a file, which is how an unquoted path with spaces
    /// resolves. Where no prefix is a file, the answer keeps whether Windows refused any of them,
    /// because only "absent" everywhere proves the uninstaller gone.</para>
    /// </summary>
    /// <param name="probeFile">Asks whether a file is there, keeping a refusal apart from absence.</param>
    public static UninstallCommand Parse(string? text, Func<string, PathPresence> probeFile)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return MissingCommand.Instance;
        }

        text = text.Trim();

        return text[0] == '"' ? Quoted(text, probeFile) : Unquoted(text, probeFile);
    }

    /// <summary>The command as the entry records it.</summary>
    public abstract string Text { get; }

    private static UninstallCommand Quoted(string text, Func<string, PathPresence> probeFile)
    {
        var close = text.IndexOf('"', 1);

        if (close < 2)
        {
            return new UnparsedCommand(text);
        }

        var executable = text[1..close];
        var arguments = text[(close + 1)..].Trim();

        return Path.IsPathFullyQualified(executable)
            ? Classify(text, executable, arguments, [(executable, probeFile(executable))])
            : Classify(text, executable, arguments, []);
    }

    private static UninstallCommand Unquoted(string text, Func<string, PathPresence> probeFile)
    {
        var candidates = new List<int>();

        foreach (Match match in ExecutableEnd().Matches(text))
        {
            candidates.Add(match.Index + match.Length);
        }

        if (candidates.Count == 0)
        {
            var space = text.IndexOf(' ');

            return space < 0 ? Classify(text, text, string.Empty, [])
                : Classify(text, text[..space], text[(space + 1)..].Trim(), []);
        }

        var probes = new List<(string Executable, PathPresence Presence)>(candidates.Count);

        foreach (var end in candidates)
        {
            var executable = text[..end];

            if (!Path.IsPathFullyQualified(executable))
            {
                // A bare name, or a relative path, which resolves against a search path this cannot
                // see. The first candidate decides: nothing after it is a path either.
                return Classify(text, executable, text[end..].Trim(), []);
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

        return Classify(text, chosen.Executable, text[chosen.Executable.Length..].Trim(), [chosen]);
    }

    /// <param name="probed">
    /// The executable's probe, or empty where it is not a fully qualified path and was not asked about.
    /// </param>
    private static UninstallCommand Classify(
        string text,
        string executable,
        string arguments,
        IReadOnlyList<(string Executable, PathPresence Presence)> probed)
    {
        if (Path.GetFileName(executable).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase)
            || executable.Equals("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            var code = ProductCodeArgument().Match(arguments);

            if (code.Success && Guid.TryParse(code.Groups["code"].Value, out var productCode))
            {
                return new InstallerCommand(text, productCode);
            }
        }

        if (probed.Count == 0)
        {
            return Path.IsPathFullyQualified(executable) || executable.Contains('\\') || executable.Contains('/')
                ? new UnparsedCommand(text)
                : new NamedCommand(text, executable, arguments);
        }

        return new ProgramCommand(text, executable, arguments, probed[0].Presence);
    }

    /// <summary>The end of each <c>.exe</c> that is followed by a space or by the end of the command.</summary>
    [GeneratedRegex(@"\.exe(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExecutableEnd();

    /// <summary><c>/I</c> or <c>/X</c> followed by a braced product code, with or without a space between.</summary>
    [GeneratedRegex(@"[/-][IX]\s*(?<code>\{[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProductCodeArgument();
}

/// <summary>The entry records no uninstall command.</summary>
public sealed record MissingCommand : UninstallCommand
{
    public static MissingCommand Instance { get; } = new();

    public override string Text => string.Empty;
}

/// <summary><c>MsiExec.exe</c> naming a product code, which Windows Installer answers for.</summary>
public sealed record InstallerCommand(string CommandText, Guid ProductCode) : UninstallCommand
{
    public override string Text => CommandText;
}

/// <summary>A fully qualified executable, and what Windows said about it.</summary>
public sealed record ProgramCommand(string CommandText, string Executable, string Arguments, PathPresence Presence)
    : UninstallCommand
{
    public override string Text => CommandText;
}

/// <summary>
/// A bare name such as <c>winget</c> or <c>rundll32.exe</c>, which Windows resolves on the search
/// path. It names a tool, not the program, so it proves nothing about whether the program is there.
/// </summary>
public sealed record NamedCommand(string CommandText, string Name, string Arguments) : UninstallCommand
{
    public override string Text => CommandText;
}

/// <summary>A command Deguffer cannot read an executable from.</summary>
public sealed record UnparsedCommand(string CommandText) : UninstallCommand
{
    public override string Text => CommandText;
}
