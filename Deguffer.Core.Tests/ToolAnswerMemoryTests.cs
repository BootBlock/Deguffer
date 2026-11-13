using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A tool's answer about where it keeps something is kept for as long as everything it read to give
/// it reads the same, and asked again the moment any of it does not (#447).
///
/// <para>The stamps are sizes, times and presence, so §6.3 cannot be asserted through them by the
/// form of a path: <see cref="InputStamp"/> reads through <see cref="LongPath.Extended"/> like every
/// other probe.</para>
/// </summary>
public sealed class ToolAnswerMemoryTests : IDisposable
{
    private const string Key = "tool where|C:\\tools\\tool.exe";

    private readonly TempDirectory _temp = new();
    private readonly ManualTimeProvider _clock = new();
    private readonly ToolAnswerMemory _memory;
    private readonly Dictionary<string, string> _environment = new(StringComparer.OrdinalIgnoreCase) { ["PATH"] = @"C:\tools" };
    private readonly string _config;
    private readonly string _absent;
    private readonly string _folder;
    private int _asked;
    private string _answer = @"C:\cache\one";

    public ToolAnswerMemoryTests()
    {
        _memory = new ToolAnswerMemory(_clock);
        _config = Path.Combine(_temp.Path, "tool.ini");
        _absent = Path.Combine(_temp.Path, "absent.ini");
        _folder = _temp.CreateDirectory("tool.d");
        File.WriteAllText(_config, "cache=one");
        File.WriteAllText(Path.Combine(_folder, "a.yml"), "a");

        // Every ask starts after the fixture was written, as a real one starts long after a
        // configuration file was last saved.
        _clock.Advance(DateTimeOffset.UtcNow - _clock.GetUtcNow() + TimeSpan.FromMinutes(1));
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task ASecondAskWithNothingChangedIsAnsweredFromMemory()
    {
        Assert.Equal(@"C:\cache\one", await AskAsync());

        _answer = @"C:\cache\two";

        Assert.Equal(@"C:\cache\one", await AskAsync());
        Assert.Equal(1, _asked);
    }

    [Fact]
    public async Task AsksAgainWhenADeclaredFileIsWritten()
    {
        await AskAsync();
        File.WriteAllText(_config, "cache=two, which is longer");
        _answer = @"C:\cache\two";

        Assert.Equal(@"C:\cache\two", await AskAsync());
        Assert.Equal(2, _asked);
    }

    [Fact]
    public async Task AsksAgainWhenADeclaredFileIsWrittenAtTheSameSize()
    {
        await AskAsync();
        File.WriteAllText(_config, "cache=two");
        File.SetLastWriteTimeUtc(_config, DateTime.UtcNow.AddSeconds(5));

        await AskAsync();

        Assert.Equal(2, _asked);
    }

    [Fact]
    public async Task AsksAgainWhenADeclaredFileIsReplacedByACopyKeepingItsSizeAndWriteTime()
    {
        await AskAsync();
        var written = File.GetLastWriteTimeUtc(_config);
        var copy = Path.Combine(_temp.Path, "copy.ini");
        File.WriteAllText(copy, "cache=two");
        File.SetLastWriteTimeUtc(copy, written);
        File.SetCreationTimeUtc(copy, written.AddSeconds(7));
        File.Move(copy, _config, overwrite: true);

        await AskAsync();

        Assert.Equal(2, _asked);
    }

    [Fact]
    public async Task AsksAgainWhenAFileDeclaredAbsentIsCreated()
    {
        await AskAsync();
        File.WriteAllText(_absent, "cache=two");

        await AskAsync();

        Assert.Equal(2, _asked);
    }

    [Fact]
    public async Task AsksAgainWhenADeclaredFileIsDeleted()
    {
        await AskAsync();
        File.Delete(_config);

        await AskAsync();

        Assert.Equal(2, _asked);
    }

    [Fact]
    public async Task AsksAgainWhenAListedFolderGainsAnEntry()
    {
        await AskAsync();
        File.WriteAllText(Path.Combine(_folder, "b.yml"), "b");

        await AskAsync();

        Assert.Equal(2, _asked);
    }

    [Fact]
    public async Task AsksAgainWhenAnEntryOfAListedFolderIsRewritten()
    {
        await AskAsync();
        File.WriteAllText(Path.Combine(_folder, "a.yml"), "a, rewritten");

        await AskAsync();

        Assert.Equal(2, _asked);
    }

    [Fact]
    public async Task AsksAgainWhenAFolderInAListedFolderGainsAnEntry()
    {
        var inner = Directory.CreateDirectory(Path.Combine(_folder, "inner")).FullName;
        TempDirectory.Age(inner, TimeSpan.FromHours(1));
        TempDirectory.Age(_folder, TimeSpan.FromHours(1));

        await AskAsync();

        // NTFS brings the folder's entry in its parent's index up to date some time after the folder
        // changes, so only a read of the folder itself sees the new time at once.
        File.WriteAllText(Path.Combine(inner, "added.yml"), "added");

        await AskAsync();

        Assert.Equal(2, _asked);
    }

    [Fact]
    public async Task AsksAgainWhenAnEntryTwoLevelsDownIsRewrittenInAFolderListedThatDeep()
    {
        var inner = Directory.CreateDirectory(Path.Combine(_folder, "inner")).FullName;
        var deep = Path.Combine(inner, "deep.yml");
        File.WriteAllText(deep, "deep");
        _clock.Advance(TimeSpan.FromMinutes(1));
        var inputs = new ToolInputs { Folders = [new ListedFolder(_folder, Depth: 2)] };

        await AskAsync(inputs);
        File.WriteAllText(deep, "deep, rewritten");
        await AskAsync(inputs);

        Assert.Equal(2, _asked);
    }

    [Fact]
    public async Task AsksAgainWhenAVariableTheToolIsGivenIsSet()
    {
        await AskAsync();
        _environment["npm_config_cache"] = @"D:\elsewhere";

        await AskAsync();

        Assert.Equal(2, _asked);
    }

    [Fact]
    public async Task AsksAgainWhenAVariableChangesOnlyInValue()
    {
        await AskAsync();
        _environment["PATH"] = @"C:\other";

        await AskAsync();

        Assert.Equal(2, _asked);
    }

    [Fact]
    public async Task AsksAgainWhenAFileTheAnswerNamedChanges()
    {
        var named = Path.Combine(_temp.Path, "named.ini");
        File.WriteAllText(named, "prefix=one");
        _answer = named;

        await AskAsync(static answer => new ToolInputs { Files = [answer] });
        File.WriteAllText(named, "prefix=two, which is longer");
        await AskAsync(static answer => new ToolInputs { Files = [answer] });

        Assert.Equal(2, _asked);
    }

    [Fact]
    public async Task DoesNotKeepAnAnswerTheToolDidNotGive()
    {
        await AskAsync(isAnswer: false);
        await AskAsync(isAnswer: false);

        Assert.Equal(2, _asked);
        Assert.False(_memory.Holds(Key));
    }

    [Fact]
    public async Task DoesNotKeepAnAnswerWhoseInputWasWrittenWhileTheToolRan()
    {
        var question = Question(Inputs(), isAnswer: true, during: () =>
        {
            File.WriteAllText(_config, "cache=two");
            File.SetLastWriteTimeUtc(_config, _clock.GetUtcNow().UtcDateTime);
        });

        await _memory.AskAsync(question, CancellationToken.None);

        Assert.False(_memory.Holds(Key));
    }

    [Fact]
    public async Task DoesNotKeepAnAnswerWhoseInputWasWrittenJustBeforeTheToolStartedOnACoarseClock()
    {
        File.SetLastWriteTimeUtc(_config, _clock.GetUtcNow().UtcDateTime - TimeSpan.FromSeconds(1));

        await AskAsync();

        Assert.False(_memory.Holds(Key));
    }

    [Fact]
    public async Task DoesNotKeepAnAnswerWhoseInputCouldNotBeRead()
    {
        using var denied = DeniedDirectory.WithUnreadableFile(_config);
        Assert.Equal(PathPresence.Refused, InputStamp.Of(_config).Presence);

        await AskAsync();
        await AskAsync();

        Assert.Equal(2, _asked);
    }

    [Fact]
    public async Task DoesNotKeepAnAnswerWhoseInputIsNamedRelatively()
    {
        await AskAsync(new ToolInputs { Files = ["tool.ini"] });
        await AskAsync(new ToolInputs { Files = ["tool.ini"] });

        Assert.Equal(2, _asked);
    }

    [Fact]
    public async Task AFailedAskIsNotKeptAndTheNextAsksAgain()
    {
        var failing = Question(Inputs(), isAnswer: true, during: () =>
        {
            _asked++;
            throw new InvalidOperationException("the tool crashed");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => _memory.AskAsync(failing, CancellationToken.None));
        Assert.Equal(@"C:\cache\one", await AskAsync());

        Assert.Equal(2, _asked);
    }

    [Fact]
    public async Task AnAnswerNoLongerGivenIsDroppedRatherThanKeptBesideTheFailure()
    {
        await AskAsync();
        File.WriteAllText(_config, "cache=two, which is longer");

        await AskAsync(isAnswer: false);

        Assert.False(_memory.Holds(Key));
    }

    [Fact]
    public async Task AskingAfreshStartsTheToolWhateverIsRememberedAndKeepsTheNewAnswer()
    {
        await AskAsync();
        _answer = @"C:\cache\two";

        Assert.Equal(@"C:\cache\two", await _memory.AskAfreshAsync(Question(Inputs()), CancellationToken.None));
        Assert.Equal(@"C:\cache\two", await AskAsync());
        Assert.Equal(2, _asked);
    }

    [Fact]
    public async Task AnotherKeyIsAnotherQuestion()
    {
        await AskAsync();

        await _memory.AskAsync(Question(Inputs()) with { Key = "tool where|D:\\tools\\tool.exe" }, CancellationToken.None);

        Assert.Equal(2, _asked);
    }

    [Fact]
    public void TheSharedRunnerSharesOneMemoryAndAnyOtherRunnerHasItsOwn()
    {
        var fake = new FakeProcessRunner();

        Assert.Same(ToolAnswerMemory.Shared, ToolAnswerMemory.For(null));
        Assert.Same(ToolAnswerMemory.Shared, ToolAnswerMemory.For(ProcessRunner.Default));
        Assert.NotSame(ToolAnswerMemory.Shared, ToolAnswerMemory.For(fake));
        Assert.NotSame(ToolAnswerMemory.For(fake), ToolAnswerMemory.For(fake));
    }

    private ToolInputs Inputs() => new()
    {
        Files = [_config, _absent],
        Folders = [new ListedFolder(_folder)],
    };

    private Task<string> AskAsync(bool isAnswer = true) => AskAsync(Inputs(), isAnswer);

    private Task<string> AskAsync(ToolInputs inputs, bool isAnswer = true) =>
        _memory.AskAsync(Question(inputs, isAnswer), CancellationToken.None);

    private Task<string> AskAsync(Func<string, ToolInputs> inputsOf) =>
        _memory.AskAsync(
            new ToolQuestion<string>(Key, _environment.ToDictionary(StringComparer.OrdinalIgnoreCase), inputsOf, static _ => true, AnswerAsync),
            CancellationToken.None);

    private ToolQuestion<string> Question(ToolInputs inputs, bool isAnswer = true, Action? during = null) => new(
        Key,
        _environment.ToDictionary(StringComparer.OrdinalIgnoreCase),
        _ => inputs,
        _ => isAnswer,
        ct =>
        {
            during?.Invoke();
            return AnswerAsync(ct);
        });

    private Task<string> AnswerAsync(CancellationToken ct)
    {
        _asked++;
        return Task.FromResult(_answer);
    }
}
