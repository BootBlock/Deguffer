using Deguffer.Core.Startup;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the Startup row menu hands to Windows for an entry, or the sentence it gives instead (§7.5).
/// A file that cannot be shown is a sentence, never a path File Explorer would answer with some other
/// folder.
/// </summary>
public sealed class StartupFilesTests
{
    private const string Tool = @"C:\Program Files\Tool\tool.exe";
    private const string UserFolder = @"C:\Users\testuser\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup";

    [Fact]
    public void AProgramThatIsThereIsHandedOver()
    {
        var program = StartupFiles.Program(Entry(StartupSource.UserRun, "Tool", Tool, ProgramStanding.Present));

        Assert.Equal(Tool, program.Value);
        Assert.Null(program.Refusal);
    }

    /// <summary>Nothing says the program is missing, so it is handed over and Windows answers for it.</summary>
    [Fact]
    public void AProgramWindowsWouldNotSayAboutIsHandedOver() =>
        Assert.Equal(Tool, StartupFiles.Program(Entry(StartupSource.UserRun, "Tool", Tool, ProgramStanding.Unknown)).Value);

    [Fact]
    public void AProgramWindowsSaysIsNotThereIsASentence()
    {
        var program = StartupFiles.Program(Entry(StartupSource.UserRun, "Tool", Tool, ProgramStanding.Missing));

        Assert.Null(program.Value);
        Assert.Equal($"Windows says {Tool} is not there, so there is no file to show.", program.Refusal);
    }

    [Fact]
    public void AProgramDegufferCannotNameIsASentence()
    {
        var program = StartupFiles.Program(Entry(StartupSource.UserRun, "Tool", program: null, ProgramStanding.Unknown));

        Assert.Null(program.Value);
        Assert.Equal("Deguffer cannot tell which program this entry starts, so there is no file to show.", program.Refusal);
    }

    [Theory]
    [InlineData(StartupSource.UserStartupFolder)]
    [InlineData(StartupSource.CommonStartupFolder)]
    public void AStartupFolderEntryHasItsItemInItsFolder(StartupSource source)
    {
        var entry = Entry(source, "Tool.lnk", Tool, ProgramStanding.Present);

        Assert.True(StartupFiles.HasItem(entry));
        Assert.Equal(UserFolder + @"\Tool.lnk", StartupFiles.Item(entry, UserFolder)?.Value);
    }

    /// <summary>A <c>Run</c> value or a packaged task is not a file in a folder, so there is no item to offer at all.</summary>
    [Theory]
    [InlineData(StartupSource.UserRun)]
    [InlineData(StartupSource.MachineRun)]
    [InlineData(StartupSource.MachineRun32)]
    [InlineData(StartupSource.PackagedTask)]
    public void AnEntryFromAnyOtherSourceHasNoItem(StartupSource source)
    {
        var entry = Entry(source, "Tool", Tool, ProgramStanding.Present);

        Assert.False(StartupFiles.HasItem(entry));
        Assert.Null(StartupFiles.Item(entry, UserFolder));
    }

    [Fact]
    public void AStartupFolderWindowsWouldNotLocateIsASentence()
    {
        var item = StartupFiles.Item(Entry(StartupSource.UserStartupFolder, "Tool.lnk", Tool, ProgramStanding.Present), folder: null);

        Assert.Null(item?.Value);
        Assert.Equal("Windows would not say where this Startup folder is, so there is no file to show.", item?.Refusal);
    }

    [Fact]
    public void TheCommandIsCopiedAsTheEntryRecordsIt()
    {
        var entry = Entry(StartupSource.UserRun, "Tool", Tool, ProgramStanding.Present) with { Command = $"\"{Tool}\" --tray" };

        Assert.Equal($"\"{Tool}\" --tray", StartupFiles.Command(entry).Value);
    }

    /// <summary>A packaged task records no command, so what it names is its program.</summary>
    [Fact]
    public void AnEntryWithNoCommandCopiesItsProgram() =>
        Assert.Equal(Tool, StartupFiles.Command(Entry(StartupSource.PackagedTask, "Tool", Tool, ProgramStanding.Present)).Value);

    [Fact]
    public void AnEntryThatNamesNothingHasNothingToCopy()
    {
        var command = StartupFiles.Command(Entry(StartupSource.UserRun, "Tool", program: null, ProgramStanding.Unknown));

        Assert.Null(command.Value);
        Assert.Equal("This entry does not name what it runs in a way Deguffer can read, so there is nothing to copy.", command.Refusal);
    }

    private static StartupEntry Entry(StartupSource source, string name, string? program, ProgramStanding standing) =>
        new(new StartupKey(source, name), name, Command: null, program, standing, Publisher: null, StartupState.Starts, Approval: null);
}
