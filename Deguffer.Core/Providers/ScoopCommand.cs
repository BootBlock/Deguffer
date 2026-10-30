using System.Text;
using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// The <c>scoop.cmd</c> shim, and the install of Scoop it starts.
///
/// <para><b>The shim, not whatever <c>PATH</c> finds first.</b> Scoop's shim folder holds
/// <c>scoop.cmd</c>, <c>scoop.ps1</c> and an extensionless <c>scoop</c> for POSIX shells. A
/// <c>PATH</c> search tries <c>PATHEXT</c>'s extensions in order, so it finds <c>scoop.ps1</c> first
/// on a machine that lists <c>.PS1</c> ahead of <c>.CMD</c>, and Deguffer starts a <c>.ps1</c> as
/// no program at all. So the step starts the <c>scoop.cmd</c> beside what was found, which the
/// runner starts through <c>cmd.exe</c>.</para>
///
/// <para><b>Why its first line is read.</b> Scoop's root defaults to four folders above its own
/// <c>lib</c>, and its portable settings file sits there too, so where the running Scoop lives decides
/// where its cache is. Every <c>scoop.cmd</c> Scoop writes opens with <c>@rem</c> and the full path of
/// the <c>scoop.ps1</c> it runs: the installer's <c>Import-ScoopShim</c> (ScoopInstaller/Install
/// <c>install.ps1</c> at <c>1e2f334083d609986d8c8bc9e31ae8e87c39fab4</c>) and Scoop's own
/// <c>shim</c> (<c>lib/core.ps1</c> at <c>e6aa3b366bdee8ed138c1e0f7b85192ebdd35d0f</c>), which
/// rewrites it on update. Reading the path the shim runs, rather than assuming the shim sits in the
/// root's <c>shims</c> folder, is what keeps a shim copied elsewhere from naming the wrong root.</para>
/// </summary>
internal static partial class ScoopCommand
{
    public const string FileName = "scoop.cmd";

    /// <summary>Far beyond the dozen short lines either writer produces.</summary>
    private const int MaximumBytes = 16 * 1024;

    /// <summary>The shim in the folder <paramref name="found"/> was found in.</summary>
    public static string Beside(string found) => Path.Combine(Path.GetDirectoryName(found)!, FileName);

    /// <summary>
    /// The root of the Scoop install <paramref name="command"/> starts, the folder holding
    /// <c>apps\scoop\current</c>, or null with the sentence saying why it could not be told.
    ///
    /// <para>Only the layout both writers install, <c>&lt;root&gt;\apps\scoop\current\bin\scoop.ps1</c>,
    /// is followed. Scoop reads its portable settings only from that layout, and a script anywhere
    /// else puts the root wherever four parent folders happen to lead.</para>
    /// </summary>
    public static string? InstallRoot(string command, out string why)
    {
        why = string.Empty;

        switch (LongPath.ProbeFile(command))
        {
            case PathPresence.Absent:
                why = $"Scoop was found on your PATH, but there is no {FileName} beside it at '{command}'. "
                    + "That is the form of Scoop's command Deguffer runs, so Scoop's cache is left alone.";
                return null;

            case PathPresence.Refused:
                why = $"{new UnreadFile(command, Unreached: true).Opening("Scoop's command")}, so Deguffer "
                    + "cannot tell which Scoop it runs, and Scoop's cache is left alone.";
                return null;
        }

        if (BoundedFile.Read(command, MaximumBytes) is not { } content)
        {
            why = $"{new UnreadFile(command, Unreached: false).Opening("Scoop's command")}, so Deguffer "
                + "cannot tell which Scoop it runs, and Scoop's cache is left alone.";
            return null;
        }

        var firstLine = Encoding.UTF8.GetString(content.Span).Split('\n')[0].TrimEnd('\r');
        var root = ScriptLine().Match(firstLine) is { Success: true } match
            ? ScoopPath.Followed(match.Groups["root"].Value)
            : null;

        if (root is null)
        {
            why = $"'{command}' does not start Scoop from a folder laid out as Scoop installs itself, so "
                + "Deguffer cannot tell where that Scoop keeps its cache, and leaves it alone.";
        }

        return root;
    }

    [GeneratedRegex(
        @"\A@rem (?<root>.+)\\apps\\scoop\\current\\bin\\scoop\.ps1\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptLine();
}
