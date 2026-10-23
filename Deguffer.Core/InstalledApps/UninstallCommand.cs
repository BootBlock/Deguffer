using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.InstalledApps;

/// <summary>
/// What an entry's <c>UninstallString</c> runs, as far as the machine can tell (§7.3).
/// </summary>
public abstract partial record UninstallCommand
{
    /// <summary>
    /// Read <paramref name="text"/> the way Windows would run it, as <see cref="CommandLine.Read"/>
    /// reads every command, and say which kind of command it is.
    /// </summary>
    /// <param name="probeFile">Asks whether a file is there, keeping a refusal apart from absence.</param>
    public static UninstallCommand Parse(string? text, Func<string, PathPresence> probeFile)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return MissingCommand.Instance;
        }

        text = text.Trim();

        return CommandLine.Read(text, probeFile) is { } command ? Classify(text, command) : new UnparsedCommand(text);
    }

    /// <summary>The command as the entry records it.</summary>
    public abstract string Text { get; }

    private static UninstallCommand Classify(string text, CommandLine command)
    {
        var (executable, arguments, presence) = command;

        if (Path.GetFileName(executable).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase)
            || executable.Equals("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            var code = ProductCodeArgument().Match(arguments);

            if (code.Success && Guid.TryParse(code.Groups["code"].Value, out var productCode))
            {
                return new InstallerCommand(text, productCode);
            }
        }

        if (presence is not { } probed)
        {
            return Path.IsPathFullyQualified(executable) || executable.Contains('\\') || executable.Contains('/')
                ? new UnparsedCommand(text)
                : new NamedCommand(text, executable, arguments);
        }

        return new ProgramCommand(text, executable, arguments, probed);
    }

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
