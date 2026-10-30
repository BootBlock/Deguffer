using System.Text.RegularExpressions;

namespace Deguffer.Core.Tests;

/// <summary>
/// §2: "Deguffer never starts, stops, pauses or reconfigures a service."
///
/// <para>These read Deguffer's own source, because no test can drive the service Deguffer
/// <em>does not</em> stop. A provider that wanted a file a service holds open would reach for one of
/// three things: a Win32 call into the service control manager, a .NET type that wraps those calls, or
/// a command such as <c>sc.exe</c> or <c>Stop-Service</c>. Each fails here the moment it is written,
/// in either project.</para>
/// </summary>
public sealed partial class ServiceControlTests
{
    /// <summary>
    /// Every call that opens a service, changes its state or changes its configuration, by the name
    /// Windows exports it under without its <c>A</c> or <c>W</c>, because a declaration may name either
    /// form or neither. Listing what runs needs none of them.
    /// </summary>
    private static readonly HashSet<string> NeverDeclared = new(StringComparer.Ordinal)
    {
        "OpenService", "StartService", "ControlService", "ControlServiceEx", "ChangeServiceConfig",
        "ChangeServiceConfig2", "CreateService", "DeleteService", "LockServiceDatabase",
    };

    private const string ServiceManager = "OpenSCManager";

    [Fact]
    public void NoCallThatControlsAServiceIsDeclared()
    {
        foreach (var declaration in ProductSources.Win32Imports())
        {
            Assert.False(
                NeverDeclared.Contains(WithoutCharacterSet(declaration.Key)),
                $"{declaration.Key} is declared in {string.Join(", ", declaration)}. §2 rules out service control: "
                + "a location a running service holds is reclaimed through its owner's route, or not offered.");
        }
    }

    /// <summary>
    /// The service control manager is declared once, in one file, and opened there once, for
    /// <c>SC_MANAGER_ENUMERATE_SERVICE</c> alone. A handle with more access is the first step towards
    /// a service Deguffer could control.
    /// </summary>
    [Fact]
    public void TheOnlyServiceManagerHandleCanListAndNothingElse()
    {
        var file = Path.Combine("Deguffer.Core", "Memory", "ServiceTableReader.cs");

        Assert.Equal(
            file,
            Assert.Single(ProductSources.Win32Imports()
                .Where(declaration => WithoutCharacterSet(declaration.Key) == ServiceManager)
                .SelectMany(declaration => declaration)));

        var source = File.ReadAllText(Path.Combine(MarkdownGuide.RepositoryRoot, file));

        Assert.Contains("private const uint EnumerateService = 0x0004;", source, StringComparison.Ordinal);

        // The declaration and one call are the only lines of code that name it, so a second call
        // cannot ask for more access in a form the call's own pattern does not match.
        Assert.Equal(2, source.Split('\n').Count(line =>
            ManagerMention().IsMatch(line) && !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        Assert.Equal("EnumerateService", Assert.Single(ManagerOpen().Matches(source)).Groups["access"].Value);
    }

    /// <summary>
    /// No line of code names a service command or the .NET type that controls services. Comments are
    /// left out, because they name these to explain why Deguffer never uses them.
    /// </summary>
    [Fact]
    public void NoCommandOrTypeThatControlsAServiceIsNamed()
    {
        var files = ProductSources.Files().ToList();

        // A sweep that read nothing would pass, so it has to have read the file that lists services.
        Assert.Contains(files, file => file.EndsWith("ServiceTableReader.cs", StringComparison.Ordinal));

        var named = files
            .SelectMany(file => File.ReadLines(file)
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Select(line => ServiceCommand().Match(line))
                .Where(match => match.Success)
                .Select(match => $"{Path.GetRelativePath(MarkdownGuide.RepositoryRoot, file)}: {match.Value}"))
            .ToList();

        Assert.Empty(named);
    }

    [GeneratedRegex(@"\bOpenSCManager\(\s*null\s*,\s*null\s*,\s*(?<access>\w+)\s*\)")]
    private static partial Regex ManagerOpen();

    [GeneratedRegex(@"\bOpenSCManager[AW]?\s*\(")]
    private static partial Regex ManagerMention();

    /// <summary>A Win32 name without the <c>A</c> or <c>W</c> that picks its character set.</summary>
    private static string WithoutCharacterSet(string name) =>
        name.EndsWith('W') || name.EndsWith('A') ? name[..^1] : name;

    /// <summary>
    /// <c>sc.exe</c>, <c>net.exe</c> and <c>net1.exe</c>, PowerShell's service cmdlets, WMI's service
    /// classes, whose methods start, stop, change and delete a service, those methods by name, and
    /// <c>System.ServiceProcess</c> with its <c>ServiceController</c>.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:sc|net1?)\.exe\b|\b(?:Start|Stop|Restart|Suspend|Resume|Set|New|Remove)-Service\b|\b(?:Win32_(?:Base)?Service|CIM_Service)\b|\b(?:Start|Stop|Pause|Resume|UserControl)Service\b|\bChangeStartMode\b|\bServiceController\b|\bSystem\.ServiceProcess\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ServiceCommand();
}
