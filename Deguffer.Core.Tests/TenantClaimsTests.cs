using Deguffer.Core.Providers;

namespace Deguffer.Core.Tests;

/// <summary>
/// The temporary-folder row asks every tenant for its claim at once (#1060), and reads the answers in
/// the tenants' order.
/// </summary>
public sealed class TenantClaimsTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly IReadOnlyList<string> Folders = [@"C:\Users\testuser\AppData\Local\Temp"];

    /// <summary>
    /// Two claims worked out synchronously, as a tenant's survey is, each blocking until the other has
    /// been asked. Asked one after another, or started together on the caller's thread, the first
    /// blocks the second from ever being asked.
    /// </summary>
    [Fact]
    public async Task SynchronousClaimsAreAskedBeforeEitherAnswers()
    {
        using var bothAsked = new CountdownEvent(2);

        IReadOnlyList<string> Survey(string entry)
        {
            bothAsked.Signal();
            return bothAsked.Wait(Deadline) ? [entry] : throw new TimeoutException("The other claim was never asked.");
        }

        var first = new Tenant("First", () => Task.FromResult(Survey(@"C:\Users\testuser\AppData\Local\Temp\first")));
        var second = new Tenant("Second", () => Task.FromResult(Survey(@"C:\Users\testuser\AppData\Local\Temp\second")));

        var claims = await TenantClaims.ReadAsync([first, second], Folders, CancellationToken.None).WaitAsync(Deadline * 2);

        Assert.Equal(["First", "Second"], claims.Rows);
    }

    /// <summary>
    /// A claim that awaits a tool, as NuGet's does, is asked beside the others too: the first answers
    /// only once the second has been asked.
    /// </summary>
    [Fact]
    public async Task AnAwaitingClaimDoesNotHoldBackTheNext()
    {
        var secondAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new Tenant("First", async () =>
        {
            await secondAsked.Task;
            return [@"C:\Users\testuser\AppData\Local\Temp\first"];
        });
        var second = new Tenant("Second", () =>
        {
            secondAsked.SetResult();
            return Task.FromResult<IReadOnlyList<string>>([@"C:\Users\testuser\AppData\Local\Temp\second"]);
        });

        var claims = await TenantClaims.ReadAsync([first, second], Folders, CancellationToken.None).WaitAsync(Deadline);

        Assert.Equal(["First", "Second"], claims.Rows);
    }

    /// <summary>
    /// The row names come in the tenants' order whatever order the answers arrive in, a tenant that
    /// claims nothing is not named, and the claimed set is every entry claimed, matched as the row
    /// matches a live entry: whatever its case, and with or without a trailing separator.
    /// </summary>
    [Fact]
    public async Task AnswersAreReadInTheTenantsOrder()
    {
        var lastAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new Tenant("Slow", async () =>
        {
            await lastAsked.Task;
            await Task.Delay(50);
            return [@"C:\Users\testuser\AppData\Local\Temp\slow", @"C:\Users\testuser\AppData\Local\Temp\slow-too"];
        });
        var none = new Tenant("None", () => Task.FromResult<IReadOnlyList<string>>([]));
        var fast = new Tenant("Fast", () =>
        {
            lastAsked.SetResult();
            return Task.FromResult<IReadOnlyList<string>>([@"C:\Users\testuser\AppData\Local\Temp\fast"]);
        });

        var claims = await TenantClaims.ReadAsync([slow, none, fast], Folders, CancellationToken.None).WaitAsync(Deadline);

        Assert.Equal(["Slow", "Fast"], claims.Rows);
        Assert.Equal(3, claims.Paths.Count);
        Assert.True(claims.Contains(@"C:\Users\testuser\AppData\Local\Temp\SLOW-TOO"));
        Assert.True(claims.Contains(@"C:\Users\testuser\AppData\Local\Temp\fast\"));
        Assert.False(claims.Contains(@"C:\Users\testuser\AppData\Local\Temp\other"));
    }

    /// <summary>
    /// A claim that fails fails the row, as it did when the claims were asked one at a time, and only
    /// once every other claim has answered, so none is left running unobserved.
    /// </summary>
    [Fact]
    public async Task AFailedClaimFailsTheReadAfterTheOthersAnswer()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answered = false;
        var failing = new Tenant("Failing", () => throw new IOException("The survey could not list the folder."));
        var slow = new Tenant("Slow", async () =>
        {
            await release.Task;
            answered = true;
            return [];
        });

        var reading = TenantClaims.ReadAsync([failing, slow], Folders, CancellationToken.None);
        await Task.Delay(50);
        Assert.False(reading.IsCompleted);

        release.SetResult();
        await Assert.ThrowsAsync<IOException>(() => reading.WaitAsync(Deadline));
        Assert.True(answered);
    }

    private sealed class Tenant(string name, Func<Task<IReadOnlyList<string>>> claimed) : ITemporaryFolderTenant
    {
        public string Name => name;

        public Task<IReadOnlyList<string>> ClaimedEntriesAsync(IReadOnlyList<string> folders, CancellationToken ct = default) => claimed();

        public void InvalidateCaches()
        {
        }
    }
}
