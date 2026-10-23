using System.Diagnostics;
using System.Security;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Startup;

/// <summary>What a program's own version information says it is and who made it, where it says.</summary>
public sealed record ProgramDescription(string? Description, string? Company)
{
    public static ProgramDescription None { get; } = new(null, null);
}

/// <summary>
/// Asks about the program an entry starts, behind a seam so whether it is there and what it says
/// about itself are a test's to decide (G1).
/// </summary>
public interface IProgramFiles
{
    /// <summary>Whether the program's file is there, keeping a refusal apart from absence.</summary>
    PathPresence Probe(string path);

    /// <summary>What its version information says, or <see cref="ProgramDescription.None"/> where it says nothing.</summary>
    ProgramDescription Describe(string path);
}

/// <inheritdoc />
/// <remarks>
/// The publisher is the version information's <c>CompanyName</c>, which a program states about
/// itself. Windows does not document where Task Manager's publisher comes from, and on the machine
/// [startup-apps.md](../../docs/todo/startup-apps.md) records it was blank for every entry that was
/// not packaged, so the page says where Deguffer's comes from rather than claim to match it.
/// </remarks>
public sealed class WindowsProgramFiles : IProgramFiles
{
    public static WindowsProgramFiles Default { get; } = new();

    private WindowsProgramFiles()
    {
    }

    public PathPresence Probe(string path) => LongPath.ProbeFile(path);

    public ProgramDescription Describe(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(LongPath.Extended(path));

            return new ProgramDescription(Text(info.FileDescription), Text(info.CompanyName));
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or SecurityException or IOException)
        {
            return ProgramDescription.None;
        }
    }

    /// <summary>A field with nothing but spaces in it says nothing, as an absent one does.</summary>
    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
