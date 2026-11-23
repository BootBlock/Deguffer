using Deguffer.Core.Health.Smartmontools;

namespace Deguffer.Core.Tests;

/// <summary>
/// #751: reading the file format smartmontools' drive database uses. Every entry here is invented: the
/// database itself is GPL, so none of it is copied into Deguffer, its tests included.
/// </summary>
public sealed class DriveDatabaseFileTests
{
    [Fact]
    public void ReadsEntriesWithJoinedLiteralsCommentsAndEscapes()
    {
        const string text = """
            /* A block comment
               over two lines */
            { "VERSION: 9.9 test",
              "-", "-",
              "Version information",
              ""
            },
            { "Example \"Quoted\" SSDs", // a line comment
              "EXAMPLE SSD "  /* between */ "[0-9]+GB",
              "",
              "A warning\nover two lines",
              "-v 9,raw48,Power_On_Hours "
              "-v 241,raw48,Total_Written",
            },
            """;

        var entries = InventedDriveDatabase.Entries(text);

        Assert.Equal(2, entries.Count);
        Assert.Equal("Example \"Quoted\" SSDs", entries[1].Family);
        Assert.Equal("EXAMPLE SSD [0-9]+GB", entries[1].ModelPattern);
        Assert.Equal(string.Empty, entries[1].FirmwarePattern);
        Assert.Equal("A warning\nover two lines", entries[1].Warning);
        Assert.Equal("-v 9,raw48,Power_On_Hours -v 241,raw48,Total_Written", entries[1].Presets);
    }

    [Fact]
    public void ABackslashAtTheEndOfALineCarriesTheLiteralOn()
    {
        var entries = InventedDriveDatabase.Entries("{ \"Fam\\\r\nily\", \"M\", \"\", \"\", \"\" }");

        Assert.Equal("Family", Assert.Single(entries).Family);
    }

    [Fact]
    public void AnEmptyFileHasNoEntries() => Assert.Empty(InventedDriveDatabase.Entries("// nothing but a comment\n"));

    [Theory]
    [InlineData("{ \"a\", \"b\", \"c\", \"d\", \"e\" }\n{ \"a\", \"b\", \"c\", \"d\" }", 2)]
    [InlineData("{ \"a\", \"b\", \"c\", \"d\", \"e\" }\n\n{ \"a\", \"b, \"c\", \"d\", \"e\" }", 3)]
    [InlineData("{ \"a\", \"b\", \"c\", \"d\", \"e\" }\n#define X\n", 2)]
    [InlineData("{ \"a\", \"b\", \"c\", \"d\", \"e\" } { \"a\", \"b\", \"c\", \"d\", \"e\" }", 1)]
    [InlineData("{ \"a\", \"b\", \"c\", \"d\", \"e\", \"f\" }", 1)]
    [InlineData("{ \"a\\q\", \"b\", \"c\", \"d\", \"e\" }", 1)]
    [InlineData("\n/* never closed\n{ \"a\", \"b\", \"c\", \"d\", \"e\" }", 2)]
    public void OneFaultFailsTheWholeFileAtItsLine(string text, int line)
    {
        Assert.False(DriveDatabaseFile.TryRead(text, out _, out var failedLine));
        Assert.Equal(line, failedLine);
    }
}
