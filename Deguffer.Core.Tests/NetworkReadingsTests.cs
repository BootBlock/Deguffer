using Deguffer.Core.Exploring.Network;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// How the network sources are asked for one session: once each, off the calling thread, at most a
/// few at a time, and never waited for past the time limit.
/// </summary>
public sealed class NetworkReadingsTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly FakeNetwork _network = new();
    private readonly ManualTimeProvider _time = new();
    private readonly NetworkReadings _readings;

    public NetworkReadingsTests()
    {
        _readings = new NetworkReadings(_network.Access, _time);
    }

    public void Dispose()
    {
        _readings.Dispose();
        _network.Release("slow.test");
    }

    /// <summary>A computer's shares are asked for once a session, however often the box or the tree asks (G4).</summary>
    [Fact]
    public async Task AnAnswerIsAskedForOnceASession()
    {
        _network.Computer("nas.test", new ListedShare("media", ShareKind.Disk, false));

        await _readings.SharesAsync("nas.test", CancellationToken.None);
        await _readings.SharesAsync("NAS.test", CancellationToken.None);

        Assert.Equal(1, _network.Asked("nas.test"));
    }

    /// <summary>Forgetting a computer asks it again, and leaves what was kept about another computer.</summary>
    [Fact]
    public async Task ForgettingAComputerAsksItAgainAndNothingElse()
    {
        _network.Computer("nas.test").Computer("files.test").Folder(@"\\nas.test\media", "Photos");

        await _readings.SharesAsync("nas.test", CancellationToken.None);
        await _readings.SharesAsync("files.test", CancellationToken.None);
        await _readings.FoldersAsync(@"\\nas.test\media", CancellationToken.None);

        _readings.Forget(@"\\nas.test");

        await _readings.SharesAsync("nas.test", CancellationToken.None);
        await _readings.SharesAsync("files.test", CancellationToken.None);
        await _readings.FoldersAsync(@"\\nas.test\media\", CancellationToken.None);

        Assert.Equal(2, _network.Asked("nas.test"));
        Assert.Equal(1, _network.Asked("files.test"));
        Assert.Equal(2, _network.Asked(@"\\nas.test\media"));
    }

    /// <summary>
    /// A computer that does not answer is told as no answer once the time limit passes, and the call
    /// goes on: its answer, once it comes, is kept and given to the next question at once.
    /// </summary>
    [Fact]
    public async Task AComputerThatDoesNotAnswerIsToldAsNoAnswerAndItsLateAnswerIsKept()
    {
        _network.Computer("slow.test", new ListedShare("media", ShareKind.Disk, false));
        _network.Hold("slow.test");

        var asked = _readings.SharesAsync("slow.test", CancellationToken.None);

        await _time.WhenWaitingAsync(Patience);
        _time.Advance(NetworkReadings.Limit);

        Assert.Equal(NetworkFailure.NoAnswer, (await asked.WaitAsync(Patience)).Failure);

        _network.Release("slow.test");

        var later = await AnswerAsync(() => _readings.SharesAsync("slow.test", CancellationToken.None));

        Assert.Null(later.Failure);
        Assert.Single(later.Entries);
        Assert.Equal(1, _network.Asked("slow.test"));
    }

    /// <summary>A wait that is cancelled stops, and changes nothing kept: the answer still arrives for the next question.</summary>
    [Fact]
    public async Task ACancelledWaitChangesNothingKept()
    {
        _network.Computer("slow.test", new ListedShare("media", ShareKind.Disk, false));
        _network.Hold("slow.test");
        using var stop = new CancellationTokenSource();

        var asked = _readings.SharesAsync("slow.test", stop.Token);
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asked.WaitAsync(Patience));

        _network.Release("slow.test");
        var later = await AnswerAsync(() => _readings.SharesAsync("slow.test", CancellationToken.None));

        Assert.Single(later.Entries);
        Assert.Equal(1, _network.Asked("slow.test"));
    }

    /// <summary>
    /// No more than a few native calls run at once, and a slot is held for as long as the call runs,
    /// not as long as anybody waits: computers past the time limit still hold theirs (G4).
    /// </summary>
    [Fact]
    public async Task AtMostAFewCallsRunAtOnceEvenPastTheTimeLimit()
    {
        var servers = Enumerable.Range(1, 6).Select(i => $"slow{i}.test").ToList();

        foreach (var server in servers)
        {
            _network.Computer(server);
            _network.Hold(server);
        }

        var asked = servers.Select(server => _readings.SharesAsync(server, CancellationToken.None)).ToList();

        await WaitUntilAsync(() => servers.Count(server => _network.Asked(server) > 0) == 4);

        // Every wait runs out of time while the four calls still hang: the callers are told there
        // was no answer, and the calls keep their slots.
        _time.Advance(NetworkReadings.Limit);
        var answers = await Task.WhenAll(asked).WaitAsync(Patience);

        Assert.All(answers, answer => Assert.Equal(NetworkFailure.NoAnswer, answer.Failure));

        await Task.Delay(100);

        Assert.Equal(4, servers.Count(server => _network.Asked(server) > 0));

        _network.Release(servers.First(server => _network.Asked(server) > 0));

        await WaitUntilAsync(() => servers.Count(server => _network.Asked(server) > 0) == 5);

        foreach (var server in servers)
        {
            _network.Release(server);
        }
    }

    /// <summary>
    /// A refresh while a question is still being asked leaves it to finish rather than asking a
    /// second time beside it, so a slow computer's answer arrives for the refresh that asked for it.
    /// </summary>
    [Fact]
    public async Task ARefreshLeavesAQuestionStillBeingAskedToFinish()
    {
        _network.Computer("slow.test", new ListedShare("media", ShareKind.Disk, false));
        _network.Hold("slow.test");

        var first = _readings.SharesAsync("slow.test", CancellationToken.None);
        await WaitUntilAsync(() => _network.Asked("slow.test") == 1);

        _readings.Forget(@"\\slow.test");
        _readings.ForgetAll();
        var second = _readings.SharesAsync("slow.test", CancellationToken.None);
        _network.Release("slow.test");

        Assert.Single((await first.WaitAsync(Patience)).Entries);
        Assert.Single((await second.WaitAsync(Patience)).Entries);
        Assert.Equal(1, _network.Asked("slow.test"));
    }

    /// <summary>Signing in to a share drops what was kept about its computer, which was an answer to an account that could not list it.</summary>
    [Fact]
    public async Task SigningInAsksTheComputerAgain()
    {
        _network.Refusing("nas.test", NetworkFailure.NeedsSignIn);

        Assert.Equal(NetworkFailure.NeedsSignIn, (await _readings.SharesAsync("nas.test", CancellationToken.None)).Failure);

        _network.Computer("nas.test", new ListedShare("media", ShareKind.Disk, false));
        var signIn = await _readings.SignInAsync(@"\\nas.test\IPC$", 42, CancellationToken.None);

        Assert.Equal(SignInOutcome.Connected, signIn.Outcome);
        Assert.Equal([(@"\\nas.test\IPC$", (nint)42)], _network.SignIns);
        Assert.Null((await _readings.SharesAsync("nas.test", CancellationToken.None)).Failure);
    }

    /// <summary>A sign-in the reader cancelled leaves what was kept as it was.</summary>
    [Fact]
    public async Task ACancelledSignInAsksNothingAgain()
    {
        _network.Refusing("nas.test", NetworkFailure.NeedsSignIn);
        _network.SignInAnswer = NetworkSignIn.Cancelled;

        await _readings.SharesAsync("nas.test", CancellationToken.None);
        await _readings.SignInAsync(@"\\nas.test\IPC$", 0, CancellationToken.None);
        await _readings.SharesAsync("nas.test", CancellationToken.None);

        Assert.Equal(1, _network.Asked("nas.test"));
    }

    /// <summary>
    /// The answer to a question asked after the slow one was let go. Polled, because the late
    /// answer lands on the thread pool after the release, and the question it is kept for may be
    /// asked a moment before.
    /// </summary>
    private async Task<NetworkListing<T>> AnswerAsync<T>(Func<Task<NetworkListing<T>>> ask)
    {
        var started = DateTime.UtcNow;

        while (true)
        {
            var asking = ask();

            if (asking.IsCompleted)
            {
                return await asking;
            }

            if (DateTime.UtcNow - started > Patience)
            {
                throw new TimeoutException("The late answer was never kept.");
            }

            await Task.Delay(10);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var started = DateTime.UtcNow;

        while (!condition())
        {
            if (DateTime.UtcNow - started > Patience)
            {
                throw new TimeoutException("The condition never held.");
            }

            await Task.Delay(10);
        }
    }
}
