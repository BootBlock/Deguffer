using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// The race itself, with each route a task the test completes by hand, so which finishes first is
/// the test's choice rather than the machine's.
/// </summary>
public class RouteRaceTests
{
    private sealed record Answer(string From, bool Reached = true);

    /// <summary>
    /// A route that answers when the test says, and notes whether it was told to stop. Once told, it
    /// stops at once, or when the test lets it where it was built to stop slowly, as a walk part way
    /// through a listing does.
    /// </summary>
    private sealed class Route<T>(bool stopsSlowly = false)
        where T : class
    {
        private readonly TaskCompletionSource<T> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _mayStop = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void LetStop() => _mayStop.TrySetResult();

        public bool Stopped { get; private set; }

        public bool Finished { get; private set; }

        public void Answer(T value) => _answer.TrySetResult(value);

        public void Fail(Exception failure) => _answer.TrySetException(failure);

        public async Task<T> Run(CancellationToken stop)
        {
            try
            {
                return await _answer.Task.WaitAsync(stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                Stopped = true;

                if (stopsSlowly)
                {
                    await _mayStop.Task;
                }

                throw;
            }
            finally
            {
                Finished = true;
            }
        }
    }

    private static Task<Raced<Answer>> Race(Route<Answer> table, Route<Answer> walk, CancellationToken ct = default) =>
        RouteRace.FirstAsync<Answer>(async stop => await table.Run(stop), walk.Run, answer => answer.Reached, ct);

    /// <summary>
    /// The walk is not left reading the disk, or reporting progress, after its question has been
    /// answered. The race returns only once the walk has stopped, however long stopping takes.
    /// </summary>
    [Fact]
    public async Task TheTableAnsweringFirstStopsTheWalkAndWaitsForItToStop()
    {
        var table = new Route<Answer>();
        var walk = new Route<Answer>(stopsSlowly: true);
        var racing = Race(table, walk);

        table.Answer(new Answer("table"));
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        Assert.True(walk.Stopped);
        Assert.False(racing.IsCompleted);

        walk.LetStop();
        var raced = await racing;

        Assert.Equal(RaceOutcome.TableAnswered, raced.Outcome);
        Assert.Equal("table", raced.Answer.From);
        Assert.True(walk.Finished);
    }

    [Fact]
    public async Task TheWalkAnsweringFirstStopsTheTablesWait()
    {
        var table = new Route<Answer>();
        var walk = new Route<Answer>();
        var racing = Race(table, walk);

        walk.Answer(new Answer("walk"));
        var raced = await racing;

        Assert.Equal(RaceOutcome.WalkAnsweredFirst, raced.Outcome);
        Assert.Equal("walk", raced.Answer.From);
        Assert.True(table.Stopped);
    }

    /// <summary>A walk that never reached its path has not answered, so the table is waited for.</summary>
    [Fact]
    public async Task AWalkThatDidNotReachItsPathWaitsForTheTable()
    {
        var table = new Route<Answer>();
        var walk = new Route<Answer>();
        var racing = Race(table, walk);

        walk.Answer(new Answer("walk", Reached: false));
        await Task.Yield();
        Assert.False(racing.IsCompleted);

        table.Answer(new Answer("table"));
        var raced = await racing;

        Assert.Equal(RaceOutcome.TableAnswered, raced.Outcome);
        Assert.Equal("table", raced.Answer.From);
    }

    [Fact]
    public async Task ATableThatDeclinesLeavesTheWalksAnswer()
    {
        var table = new Route<Answer>();
        var walk = new Route<Answer>();
        var racing = Race(table, walk);

        table.Answer(null!);
        await Task.Yield();
        Assert.False(racing.IsCompleted);

        walk.Answer(new Answer("walk"));
        var raced = await racing;

        Assert.Equal(RaceOutcome.TableDeclined, raced.Outcome);
        Assert.Equal("walk", raced.Answer.From);
    }

    /// <summary>Unreached and declined both: the walk's answer is all there is, and says so.</summary>
    [Fact]
    public async Task AnUnreachedWalkStandsWhereTheTableDeclines()
    {
        var table = new Route<Answer>();
        var walk = new Route<Answer>();
        var racing = Race(table, walk);

        walk.Answer(new Answer("walk", Reached: false));
        table.Answer(null!);
        var raced = await racing;

        Assert.Equal(RaceOutcome.TableDeclined, raced.Outcome);
        Assert.False(raced.Answer.Reached);
    }

    /// <summary>A table that fails has not declined: the failure is a fault, and the walk does not hide it.</summary>
    [Fact]
    public async Task ATableThatFailsIsAFaultRatherThanADecline()
    {
        var table = new Route<Answer>();
        var walk = new Route<Answer>();
        var racing = Race(table, walk);

        table.Fail(new InvalidOperationException("the table is broken"));
        walk.Answer(new Answer("walk"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => racing);
    }

    [Fact]
    public async Task TheCallersCancellationStopsBothRoutes()
    {
        using var cancel = new CancellationTokenSource();
        var table = new Route<Answer>();
        var walk = new Route<Answer>();
        var racing = Race(table, walk, cancel.Token);

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => racing);
        Assert.True(table.Stopped);
        Assert.True(walk.Stopped);
    }
}
