using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A tool's answer about where it keeps something is served for as long as everything it read to give
/// it reads the same, once a reading from before an ask has confirmed it, and asked again the moment
/// any of it does not (#447).
///
/// <para>Each "asks again" case starts from a trusted answer (<see cref="TrustAsync(ToolInputs)"/>),
/// and each "is not trusted" case from a held one, so only the rule it names can decide it.</para>
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
    public async Task TheFirstAnswerIsHeldUntrustedAndTheNextAskConfirmsIt()
    {
        await AskAsync();
        Assert.False(_memory.Holds(Key));

        await AskAsync();
        Assert.True(_memory.Holds(Key));
        Assert.Equal(2, _asked);
    }

    [Fact]
    public async Task AConfirmedAnswerIsServedWithoutAskingAgain()
    {
        await TrustAsync();
        _answer = @"C:\cache\two";

        Assert.Equal(@"C:\cache\one", await AskAsync());
        Assert.Equal(0, _asked);
    }

    [Fact]
    public async Task AsksAgainWhenADeclaredFileIsWritten()
    {
        await TrustAsync();
        File.WriteAllText(_config, "cache=two, which is longer");
        _answer = @"C:\cache\two";

        Assert.Equal(@"C:\cache\two", await AskAsync());
        Assert.Equal(1, _asked);
    }

    [Fact]
    public async Task AsksAgainWhenADeclaredFileIsWrittenAtTheSameSize()
    {
        await TrustAsync();
        File.WriteAllText(_config, "cache=two");
        File.SetLastWriteTimeUtc(_config, DateTime.UtcNow.AddSeconds(5));

        await AskAsync();

        Assert.Equal(1, _asked);
    }

    [Fact]
    public async Task AsksAgainWhenADeclaredFileIsReplacedByACopyKeepingItsSizeAndWriteTime()
    {
        await TrustAsync();
        var written = File.GetLastWriteTimeUtc(_config);
        var copy = Path.Combine(_temp.Path, "copy.ini");
        File.WriteAllText(copy, "cache=two");
        File.SetLastWriteTimeUtc(copy, written);
        File.SetCreationTimeUtc(copy, written.AddSeconds(7));
        File.Move(copy, _config, overwrite: true);

        await AskAsync();

        Assert.Equal(1, _asked);
    }

    [Fact]
    public async Task AsksAgainWhenAFileDeclaredAbsentIsCreated()
    {
        await TrustAsync();
        File.WriteAllText(_absent, "cache=two");

        await AskAsync();

        Assert.Equal(1, _asked);
    }

    [Fact]
    public async Task AsksAgainWhenADeclaredFileIsDeleted()
    {
        await TrustAsync();
        File.Delete(_config);

        await AskAsync();

        Assert.Equal(1, _asked);
    }

    [Fact]
    public async Task AsksAgainWhenAListedFolderGainsAnEntry()
    {
        await TrustAsync();
        File.WriteAllText(Path.Combine(_folder, "b.yml"), "b");

        await AskAsync();

        Assert.Equal(1, _asked);
    }

    [Fact]
    public async Task AsksAgainWhenAnEntryOfAListedFolderIsRewritten()
    {
        await TrustAsync();
        File.WriteAllText(Path.Combine(_folder, "a.yml"), "a, rewritten");

        await AskAsync();

        Assert.Equal(1, _asked);
    }

    [Fact]
    public async Task AsksAgainWhenAListedFolderIsDeleted()
    {
        await TrustAsync();
        Directory.Delete(_folder, recursive: true);

        await AskAsync();

        Assert.Equal(1, _asked);
    }

    [Fact]
    public async Task AsksAgainWhenAFolderInAListedFolderGainsAnEntry()
    {
        var inner = Directory.CreateDirectory(Path.Combine(_folder, "inner")).FullName;
        TempDirectory.Age(inner, TimeSpan.FromHours(1));
        await TrustAsync();

        // NTFS brings the folder's entry in its parent's index up to date some time after the folder
        // changes, so only a read of the folder itself sees the new time at once.
        File.WriteAllText(Path.Combine(inner, "added.yml"), "added");

        await AskAsync();

        Assert.Equal(1, _asked);
    }

    [Fact]
    public async Task AsksAgainWhenAnEntryTwoLevelsDownIsRewrittenInAFolderListedThatDeep()
    {
        var inner = Directory.CreateDirectory(Path.Combine(_folder, "inner")).FullName;
        var deep = Path.Combine(inner, "deep.yml");
        File.WriteAllText(deep, "deep");
        _clock.Advance(TimeSpan.FromMinutes(1));
        var inputs = new ToolInputs { Folders = [new ListedFolder(_folder, Depth: 2)] };

        await TrustAsync(inputs);
        File.WriteAllText(deep, "deep, rewritten");
        await AskAsync(inputs);

        Assert.Equal(1, _asked);
    }

    [Fact]
    public async Task AFileAtAListedPathKeepsItsTimes()
    {
        var file = Path.Combine(_temp.Path, "listed-as-folder");
        File.WriteAllText(file, "one");
        _clock.Advance(TimeSpan.FromMinutes(1));
        var inputs = new ToolInputs { Folders = [new ListedFolder(file)] };

        await TrustAsync(inputs);
        File.WriteAllText(file, "two");
        await AskAsync(inputs);

        Assert.Equal(1, _asked);
    }

    /// <summary>
    /// A tool can leave an entry in a folder it reads and remove it again as it answers, as pnpm does in
    /// its home folder, which moves the folder's own last-write time and nothing else.
    /// </summary>
    [Fact]
    public async Task AListedFolderWhoseOwnTimeMovedWhileItsEntriesStayedTheSameIsStillTrusted()
    {
        await AskAsync();

        var question = Question(Inputs(), isAnswer: true, during: () =>
        {
            var transient = Path.Combine(_folder, "probe.tmp");
            File.WriteAllText(transient, "probe");
            File.Delete(transient);
        });

        await _memory.AskAsync(question, CancellationToken.None);

        Assert.True(_memory.Holds(Key));
    }

    [Fact]
    public async Task AsksAgainWhenAVariableTheToolIsGivenIsSet()
    {
        await TrustAsync();
        _environment["npm_config_cache"] = @"D:\elsewhere";

        await AskAsync();

        Assert.Equal(1, _asked);
    }

    [Fact]
    public async Task AsksAgainWhenAVariableChangesOnlyInValue()
    {
        await TrustAsync();
        _environment["PATH"] = @"C:\other";

        await AskAsync();

        Assert.Equal(1, _asked);
    }

    [Fact]
    public async Task AsksAgainWhenAFileTheAnswerNamedChanges()
    {
        var named = Path.Combine(_temp.Path, "named.ini");
        File.WriteAllText(named, "prefix=one");
        _answer = named;

        await AskAsync(static answer => new ToolInputs { Files = [answer] });
        await AskAsync(static answer => new ToolInputs { Files = [answer] });
        Assert.True(_memory.Holds(Key));

        File.WriteAllText(named, "prefix=two, which is longer");
        await AskAsync(static answer => new ToolInputs { Files = [answer] });

        Assert.Equal(3, _asked);
    }

    /// <summary>
    /// An answer naming other files than the last one did cannot be compared with a reading taken
    /// before the tool started, so it is held untrusted until the next ask confirms it.
    /// </summary>
    [Fact]
    public async Task AnAnswerNamingOtherFilesThanTheLastIsNotTrustedUntilConfirmed()
    {
        var first = Path.Combine(_temp.Path, "first.ini");
        var second = Path.Combine(_temp.Path, "second.ini");
        File.WriteAllText(first, "one");
        File.WriteAllText(second, "two");
        _clock.Advance(TimeSpan.FromMinutes(1));
        _answer = first;

        await AskAsync(static answer => new ToolInputs { Files = [answer] });
        await AskAsync(static answer => new ToolInputs { Files = [answer] });
        _answer = second;
        File.WriteAllText(first, "one, rewritten");
        await AskAsync(static answer => new ToolInputs { Files = [answer] });

        Assert.False(_memory.Holds(Key));
    }

    /// <summary>
    /// An answer that differs from the one it would confirm, with every declared input reading the
    /// same, came from something the inputs do not account for: an input that changed and changed back
    /// while the tool ran, or one nobody declared.
    /// </summary>
    [Fact]
    public async Task DoesNotTrustAnAnswerThatDiffersFromTheOneItWouldConfirm()
    {
        await AskAsync();
        _answer = @"C:\cache	wo";

        await AskAsync();

        Assert.False(_memory.Holds(Key));
    }

    [Fact]
    public async Task AnAnswerThatDifferedIsTrustedOnceTheNextAskGivesItAgain()
    {
        await AskAsync();
        _answer = @"C:\cache	wo";
        await AskAsync();

        Assert.Equal(@"C:\cache	wo", await AskAsync());
        Assert.True(_memory.Holds(Key));
    }

    [Fact]
    public async Task DoesNotTrustAnAnswerTheToolDidNotGive()
    {
        await AskAsync(isAnswer: false);
        await AskAsync(isAnswer: false);

        Assert.Equal(2, _asked);
        Assert.False(_memory.Holds(Key));
    }

    [Fact]
    public async Task DoesNotTrustAnAnswerWhoseInputWasWrittenWhileTheToolRan()
    {
        await AskAsync();

        var question = Question(Inputs(), isAnswer: true, during: () => File.WriteAllText(_config, "cache=two"));

        await _memory.AskAsync(question, CancellationToken.None);

        Assert.False(_memory.Holds(Key));
    }

    [Fact]
    public async Task DoesNotTrustAnAnswerWhoseInputWasDeletedWhileTheToolRan()
    {
        await AskAsync();

        // A deleted file leaves no time to compare, so only the reading from before the start shows it.
        await _memory.AskAsync(Question(Inputs(), isAnswer: true, during: () => File.Delete(_config)), CancellationToken.None);

        Assert.False(_memory.Holds(Key));
    }

    [Fact]
    public async Task DoesNotTrustAnAnswerWhoseInputWasRenamedIntoPlaceWhileTheToolRan()
    {
        var staged = Path.Combine(_temp.CreateDirectory("staging"), "absent.ini");
        File.WriteAllText(staged, "cache=two");
        _clock.Advance(TimeSpan.FromMinutes(1));
        await AskAsync();

        // A rename keeps the file's own old times, so only the reading from before the start shows it.
        await _memory.AskAsync(Question(Inputs(), isAnswer: true, during: () => File.Move(staged, _absent)), CancellationToken.None);

        Assert.True(InputStamp.Of(_absent).Newest < _clock.GetUtcNow().ToFileTime());
        Assert.False(_memory.Holds(Key));
    }

    [Fact]
    public async Task DoesNotTrustAnAnswerWhoseInputsFolderWasDeletedWholeWhileTheToolRan()
    {
        var folder = _temp.CreateDirectory("config");
        var file = Path.Combine(folder, "tool.ini");
        File.WriteAllText(file, "cache=two");
        _clock.Advance(TimeSpan.FromMinutes(1));
        var inputs = new ToolInputs { Files = [file] };
        await AskAsync(inputs);

        await _memory.AskAsync(
            Question(inputs, isAnswer: true, during: () => Directory.Delete(folder, recursive: true)),
            CancellationToken.None);

        Assert.False(_memory.Holds(Key));
    }

    [Fact]
    public async Task DoesNotTrustAnAnswerWhoseInputsFolderWasMovedInWithItsOldTimesWhileTheToolRan()
    {
        var staged = _temp.CreateDirectory("staging", "config");
        File.WriteAllText(Path.Combine(staged, "tool.ini"), "cache=two");
        var file = Path.Combine(_temp.Path, "config", "tool.ini");
        _clock.Advance(TimeSpan.FromMinutes(1));
        var inputs = new ToolInputs { Files = [file] };
        await AskAsync(inputs);

        // A move keeps the folder's times and the file's.
        await _memory.AskAsync(
            Question(inputs, isAnswer: true, during: () => Directory.Move(staged, Path.GetDirectoryName(file)!)),
            CancellationToken.None);

        Assert.True(InputStamp.Of(file).Newest < _clock.GetUtcNow().ToFileTime());
        Assert.False(_memory.Holds(Key));
    }

    /// <summary>
    /// A file system that keeps coarse times can give a write made as the tool starts a time from just
    /// before it, with the size unchanged, so the two readings would agree: an input that recent is not
    /// trusted however they compare.
    /// </summary>
    [Fact]
    public async Task DoesNotTrustAnAnswerWhoseInputWasWrittenJustBeforeTheToolStartedOnACoarseClock()
    {
        await AskAsync();
        File.SetLastWriteTimeUtc(_config, _clock.GetUtcNow().UtcDateTime - TimeSpan.FromSeconds(1));

        await AskAsync();

        Assert.False(_memory.Holds(Key));
    }

    [Fact]
    public async Task DoesNotTrustAnAnswerWhoseInputCouldNotBeRead()
    {
        using var denied = DeniedDirectory.WithUnreadableFile(_config);
        Assert.Equal(PathPresence.Refused, InputStamp.Of(_config).Presence);

        await AskAsync();
        await AskAsync();
        await AskAsync();

        Assert.Equal(3, _asked);
        Assert.False(_memory.Holds(Key));
    }

    [Fact]
    public async Task DoesNotTrustAnAnswerWhoseInputIsNamedRelatively()
    {
        await AskAsync(new ToolInputs { Files = ["tool.ini"] });
        await AskAsync(new ToolInputs { Files = ["tool.ini"] });
        await AskAsync(new ToolInputs { Files = ["tool.ini"] });

        Assert.Equal(3, _asked);
        Assert.False(_memory.Holds(Key));
    }

    [Fact]
    public async Task AFailedAskIsNotKeptAndTheNextAsksAgain()
    {
        await TrustAsync();
        File.WriteAllText(_config, "cache=two, which is longer");
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
        await TrustAsync();
        File.WriteAllText(_config, "cache=two, which is longer");

        await AskAsync(isAnswer: false);

        Assert.False(_memory.Holds(Key));
    }

    [Fact]
    public async Task AskingAfreshStartsTheToolWhateverIsTrustedAndHoldsTheNewAnswerUntilConfirmed()
    {
        await TrustAsync();
        _answer = @"C:\cache\two";

        // A different answer with every input unchanged is held, not trusted, so the next ask confirms it.
        Assert.Equal(@"C:\cache\two", await _memory.AskAfreshAsync(Question(Inputs()), CancellationToken.None));
        Assert.False(_memory.Holds(Key));
        Assert.Equal(@"C:\cache\two", await AskAsync());
        Assert.True(_memory.Holds(Key));
        Assert.Equal(@"C:\cache\two", await AskAsync());
        Assert.Equal(2, _asked);
    }

    [Fact]
    public async Task AnotherKeyIsAnotherQuestion()
    {
        await TrustAsync();

        await _memory.AskAsync(Question(Inputs()) with { Key = "tool where|D:\\tools\\tool.exe" }, CancellationToken.None);

        Assert.Equal(1, _asked);
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

    /// <summary>Asks until the answer is trusted, asserts that it is, and starts the count again.</summary>
    private Task TrustAsync() => TrustAsync(Inputs());

    private async Task TrustAsync(ToolInputs inputs)
    {
        await AskAsync(inputs);
        await AskAsync(inputs);
        Assert.True(_memory.Holds(Key));
        _asked = 0;
    }

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
