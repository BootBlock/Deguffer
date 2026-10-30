using Deguffer.Core.Providers;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The package store Conan's configuration names, read as <c>ConfDefinition.loads</c> reads
/// <c>global.conf</c> and <c>global_user.conf</c>, and refused wherever the reading is not plain,
/// because the store is judged from this reading before Conan is started.
/// </summary>
public sealed class ConanGlobalConfTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Home => Path.Combine(_temp.Path, ".conan2");

    private string Storage => Path.Combine(_temp.Path, "storage");

    private void Write(string text, string file = ConanGlobalConf.FileName)
    {
        Directory.CreateDirectory(Home);
        File.WriteAllText(Path.Combine(Home, file), text);
    }

    private string Store() => Assert.IsType<ConanConfReading.Plain>(ConanGlobalConf.Read(Home)).Store;

    private string Refusal() => Assert.IsType<ConanConfReading.Unusable>(ConanGlobalConf.Read(Home)).Why;

    [Fact]
    public void NoConfigurationIsTheHomesOwnStore()
    {
        Directory.CreateDirectory(Home);

        Assert.Equal(Path.Combine(Home, "p"), Store());
    }

    [Theory]
    [InlineData("core.cache:storage_path={0}")]
    [InlineData("  core.cache:storage_path  =   {0}  ")]
    [InlineData("# core.cache:storage_path = elsewhere\ncore.cache:storage_path = {0}")]
    [InlineData("core.cache:storage_path = {1}\r\ncore.cache:storage_path = {0}\r\n")]
    public void ReadsAPlainSetting(string text)
    {
        Write(string.Format(System.Globalization.CultureInfo.InvariantCulture, text, Storage, Path.Combine(_temp.Path, "first")));

        Assert.Equal(Storage, Store());
    }

    [Theory]
    [InlineData("core.cache:storage_path=")]
    [InlineData("core.cache:storage_path = None")]
    public void ReadsAnEmptyOrNoneValueAsTheDefault(string text)
    {
        Write(text);

        Assert.Equal(Path.Combine(Home, "p"), Store());
    }

    /// <summary><c>global_user.conf</c> is read after <c>global.conf</c>, and its settings win.</summary>
    [Fact]
    public void TheUsersFileWins()
    {
        Write($"core.cache:storage_path = {Path.Combine(_temp.Path, "shared")}");
        Write($"core.cache:storage_path = {Storage}", ConanGlobalConf.UserFileName);

        Assert.Equal(Storage, Store());
    }

    /// <summary>
    /// Python's <c>splitlines</c> and <c>strip</c> take the information separators, which .NET's do
    /// not, so a reading that missed them would judge one folder while Conan made another.
    /// </summary>
    [Fact]
    public void SplitsAndStripsLinesAsPythonDoes()
    {
        Write($"core:non_interactive = True\u001ccore.cache:storage_path = {Storage}\u001f");

        Assert.Equal(Storage, Store());
    }

    [Theory]
    [InlineData("core.cache:storage_path = {{ conan_home_folder }}/store", "is a template")]
    [InlineData("{% set store = 'x' %}", "is a template")]
    [InlineData("{# a comment Jinja removes #}", "is a template")]
    [InlineData("core.cache:storage_path += C:\\store", "Line 1 of")]
    [InlineData("core.cache:storage_path=!", "Line 1 of")]
    [InlineData("zlib/*:core.cache:storage_path = C:\\store", "Line 1 of")]
    [InlineData("Core.Cache:Storage_Path = C:\\store", "Line 1 of")]
    [InlineData("core.cache:storage_path = \"C:\\store\"", "Line 1 of")]
    [InlineData("core.cache:storage_path = store", "Line 1 of")]
    [InlineData("core.cache:storage_path = C:store", "Line 1 of")]
    [InlineData("core.cache:storage_path = []", "Line 1 of")]
    [InlineData("tools.build:jobs = 8\ncore.cache:storage_path", "Line 2 of")]
    [InlineData("tools.build:jobs = 8\r\ncore:non_interactive = True\r\ncore.cache:storage_path", "Line 3 of")]
    public void RefusesWhatIsNotOnePlainFolder(string text, string why)
    {
        Write(text);

        Assert.Contains(why, Refusal(), StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAFileThatIsNotUtf8()
    {
        Directory.CreateDirectory(Home);
        File.WriteAllBytes(Path.Combine(Home, ConanGlobalConf.UserFileName), [0x63, 0xff, 0x0a]);

        Assert.Contains("is not UTF-8 text", Refusal(), StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAFileThatWillNotBeRead()
    {
        Write($"core.cache:storage_path = {Storage}");

        using (DeniedDirectory.WithUnreadableContent(Path.Combine(Home, ConanGlobalConf.FileName)))
        {
            Assert.Contains("could not read", Refusal(), StringComparison.Ordinal);
        }
    }

    /// <summary>The clean compares the files byte for byte, the absence of one included.</summary>
    [Fact]
    public void ComparesTheFilesByteForByte()
    {
        Write("core:non_interactive = True\n");
        var before = Assert.IsType<ConanConfReading.Plain>(ConanGlobalConf.Read(Home)).Files;

        Assert.True(before.SameAs(Assert.IsType<ConanConfReading.Plain>(ConanGlobalConf.Read(Home)).Files));

        Write("core:non_interactive = True \n");
        Assert.False(before.SameAs(Assert.IsType<ConanConfReading.Plain>(ConanGlobalConf.Read(Home)).Files));

        Write("core:non_interactive = True\n");
        Write(string.Empty, ConanGlobalConf.UserFileName);
        Assert.False(before.SameAs(Assert.IsType<ConanConfReading.Plain>(ConanGlobalConf.Read(Home)).Files));
    }
}
