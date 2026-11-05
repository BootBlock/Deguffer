using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Memory;
using Deguffer.Core.Startup;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Startup row menu wires Core's answer to Windows (§7.5): what it hands over, what it says
/// instead, and how it reports a hand-off Windows refused. What is handed over is proved in Core.
/// </summary>
public sealed class StartupRowMenuTests
{
    private const string Tool = @"C:\Program Files\Tool\tool.exe";
    private const string Folder = @"C:\Users\testuser\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup";

    private readonly List<(string Action, string Value)> _handed = [];
    private readonly List<(string Sentence, InfoBarSeverity Severity)> _said = [];
    private string? _failure;

    [Fact]
    public void NothingIsOfferedWithoutARow()
    {
        var menu = Menu();

        Assert.False(menu.RevealProgramCommand.CanExecute(null));
        Assert.False(menu.RevealItemCommand.CanExecute(null));
        Assert.False(menu.PropertiesCommand.CanExecute(null));
        Assert.False(menu.CopyCommand.CanExecute(null));
        Assert.False(menu.HasItem);
    }

    [Fact]
    public void EachHandOffGetsTheRowsOwnFile()
    {
        var menu = Menu(Row(StartupSource.UserRun, "Tool", Tool, command: $"\"{Tool}\" --tray"));

        menu.RevealProgramCommand.Execute(null);
        menu.PropertiesCommand.Execute(null);
        menu.CopyCommand.Execute(null);

        Assert.Equal([("reveal", Tool), ("properties", Tool), ("copy", $"\"{Tool}\" --tray")], _handed);
        Assert.Empty(_said);
    }

    /// <summary>Only a Startup folder entry has an item in a folder, so only its menu shows one.</summary>
    [Fact]
    public void AStartupFolderEntryShowsItsItem()
    {
        var menu = Menu(Row(StartupSource.UserStartupFolder, "Tool.lnk", Tool));

        Assert.True(menu.HasItem);
        menu.RevealItemCommand.Execute(null);

        Assert.Equal([("reveal", Folder + @"\Tool.lnk")], _handed);

        menu.Row = Row(StartupSource.UserRun, "Tool", Tool);

        Assert.False(menu.HasItem);
    }

    /// <summary>§7.5: a file that cannot be shown is a sentence, and nothing reaches File Explorer.</summary>
    [Fact]
    public void AMissingProgramIsASentenceAndNothingIsHandedOver()
    {
        var menu = Menu(Row(StartupSource.UserRun, "Tool", Tool, ProgramStanding.Missing));

        menu.RevealProgramCommand.Execute(null);
        menu.PropertiesCommand.Execute(null);

        Assert.Empty(_handed);
        Assert.Equal(2, _said.Count);
        Assert.All(_said, said =>
        {
            Assert.Equal($"Windows says {Tool} is not there, so there is no file to show.", said.Sentence);
            Assert.Equal(InfoBarSeverity.Informational, said.Severity);
        });
    }

    [Fact]
    public void AHandOffWindowsRefusedSaysWhatWindowsAnswered()
    {
        _failure = "The system cannot find the file specified.";
        var menu = Menu(Row(StartupSource.UserRun, "Tool", Tool));

        menu.RevealProgramCommand.Execute(null);

        Assert.Equal([("The system cannot find the file specified.", InfoBarSeverity.Warning)], _said);
    }

    private StartupRowMenu Menu(StartupRow? row = null)
    {
        var shell = new ShellHandoff(
            path => Hand("reveal", path),
            path => Hand("properties", path),
            text => Hand("copy", text));

        return new StartupRowMenu(_ => Folder, shell, (sentence, severity) => _said.Add((sentence, severity))) { Row = row };
    }

    private string? Hand(string action, string value)
    {
        _handed.Add((action, value));
        return _failure;
    }

    private static StartupRow Row(
        StartupSource source,
        string name,
        string program,
        ProgramStanding standing = ProgramStanding.Present,
        string? command = null)
    {
        var entry = new StartupEntry(new StartupKey(source, name), name, command, program, standing, Publisher: null, StartupState.Starts, Approval: null);

        return new StartupRow(entry, StartupMarks.For(entry, running: null, ProcessFigures.Checked, isElevated: false));
    }
}
