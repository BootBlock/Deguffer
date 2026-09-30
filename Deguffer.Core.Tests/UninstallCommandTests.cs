using Deguffer.Core.InstalledApps;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// How an entry's <c>UninstallString</c> is read (§7.3). The executable it names is the evidence the
/// stale rule rests on, so a command read wrongly is an entry judged on a file nobody named.
/// </summary>
public sealed class UninstallCommandTests
{
    private static readonly Guid Product = Guid.Parse("{12345678-9ABC-DEF0-1234-56789ABCDEF0}");

    private readonly Dictionary<string, PathPresence> _files = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<string> _asked = [];

    private UninstallCommand Parse(string? text) => UninstallCommand.Parse(text, path =>
    {
        _asked.Add(path);
        return _files.GetValueOrDefault(path, PathPresence.Absent);
    });

    [Fact]
    public void NoCommandIsMissing()
    {
        Assert.IsType<MissingCommand>(Parse(null));
        Assert.IsType<MissingCommand>(Parse("   "));
    }

    [Fact]
    public void AQuotedCommandNamesTheQuotedSpan()
    {
        _files[@"C:\Program Files\Tool\unins000.exe"] = PathPresence.Present;

        var command = Assert.IsType<ProgramCommand>(Parse("\"C:\\Program Files\\Tool\\unins000.exe\" /SILENT"));

        Assert.Equal(@"C:\Program Files\Tool\unins000.exe", command.Executable);
        Assert.Equal("/SILENT", command.Arguments);
        Assert.Equal(PathPresence.Present, command.Presence);
    }

    [Fact]
    public void AnUnquotedPathWithSpacesResolvesToTheShortestPrefixThatIsAFile()
    {
        _files[@"C:\Program Files\Tool Kit\uninstall.exe"] = PathPresence.Present;

        var command = Assert.IsType<ProgramCommand>(Parse(@"C:\Program Files\Tool Kit\uninstall.exe /remove C:\other.exe"));

        Assert.Equal(@"C:\Program Files\Tool Kit\uninstall.exe", command.Executable);
        Assert.Equal(@"/remove C:\other.exe", command.Arguments);
        Assert.Equal(PathPresence.Present, command.Presence);
    }

    [Fact]
    public void AnUnquotedCommandWhoseEveryPrefixIsAbsentIsAnAbsentUninstaller()
    {
        var command = Assert.IsType<ProgramCommand>(Parse(@"C:\Gone\setup.exe /uninstall C:\Gone\log.exe"));

        Assert.Equal(PathPresence.Absent, command.Presence);
        Assert.Equal(@"C:\Gone\setup.exe", command.Executable);
    }

    /// <summary>A refusal on any prefix means the uninstaller may be there, so it is never read as absent.</summary>
    [Fact]
    public void AnUnquotedCommandWithARefusedPrefixIsRefusedRatherThanAbsent()
    {
        _files[@"C:\Tool.exe Kit\remove.exe"] = PathPresence.Refused;

        var command = Assert.IsType<ProgramCommand>(Parse(@"C:\Tool.exe Kit\remove.exe /x"));

        Assert.Equal(PathPresence.Refused, command.Presence);
        Assert.Equal(@"C:\Tool.exe Kit\remove.exe", command.Executable);
        Assert.Equal("/x", command.Arguments);
    }

    [Fact]
    public void AnUnquotedCommandStopsAskingOnceAPrefixIsAFile()
    {
        _files[@"C:\A\first.exe"] = PathPresence.Present;

        Parse(@"C:\A\first.exe C:\A\second.exe");

        Assert.Equal([@"C:\A\first.exe"], _asked);
    }

    [Theory]
    [InlineData("MsiExec.exe /X{12345678-9ABC-DEF0-1234-56789ABCDEF0}")]
    [InlineData("MsiExec.exe /I{12345678-9abc-def0-1234-56789abcdef0}")]
    [InlineData("msiexec /x {12345678-9ABC-DEF0-1234-56789ABCDEF0} /qb")]
    [InlineData("\"C:\\Windows\\System32\\msiexec.exe\" /X{12345678-9ABC-DEF0-1234-56789ABCDEF0}")]
    public void MsiExecNamingAProductCodeIsAnInstallerCommand(string text)
    {
        var command = Assert.IsType<InstallerCommand>(Parse(text));

        Assert.Equal(Product, command.ProductCode);
    }

    [Theory]
    [InlineData("winget uninstall --product-code x", "winget")]
    [InlineData("rundll32.exe dfshim.dll,ShArpMaintain app", "rundll32.exe")]
    [InlineData("MsiExec.exe /X", "MsiExec.exe")]
    public void ABareNameIsANamedCommandThatIsNeverProbed(string text, string name)
    {
        var command = Assert.IsType<NamedCommand>(Parse(text));

        Assert.Equal(name, command.Name);
        Assert.Empty(_asked);
    }

    [Theory]
    [InlineData(@"..\tool\unins000.exe /x")]
    [InlineData("\"C:\\unterminated.exe /x")]
    [InlineData(@"Tools\remove /x")]
    public void ACommandWithNoFullExecutablePathIsUnparsed(string text)
    {
        Assert.IsType<UnparsedCommand>(Parse(text));
    }
}
