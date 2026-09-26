using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Removing one scratch tree when something else is holding a file inside it open.
///
/// <para>The suite forgives a refused delete rather than fail a run over it, so a refusal that a
/// second attempt would have cleared used to strand the whole tree for good. These cases pin the
/// two halves of that: the wait recovers a transient hold, and a hold that outlasts the attempts is
/// still forgiven rather than thrown.</para>
/// </summary>
public sealed class ScratchTreeTests
{
    [Fact]
    public void RemovesTheTreeAndSaysSo()
    {
        using var temp = new TempDirectory();
        var tree = temp.CreateDirectory("gone");
        File.WriteAllBytes(Path.Combine(tree, "content.bin"), new byte[8]);

        Assert.True(ScratchTree.TryRemove(tree, RemovalAttempts.One));
        Assert.False(Directory.Exists(tree));
    }

    [Fact]
    public void TreatsATreeThatIsAlreadyGoneAsRemoved()
    {
        using var temp = new TempDirectory();

        Assert.True(ScratchTree.TryRemove(Path.Combine(temp.Path, "never-made"), RemovalAttempts.One));
    }

    [Fact]
    public void ForgivesAHoldThatOutlastsTheAttempts()
    {
        using var temp = new TempDirectory();
        var tree = temp.CreateDirectory("held");

        using (Hold(tree))
        {
            Assert.False(ScratchTree.TryRemove(tree, RemovalAttempts.One));
        }

        Assert.True(Directory.Exists(tree));
    }

    /// <summary>
    /// The case the retry exists for: the handle goes while the attempts are still running.
    ///
    /// <para>The hold is released well inside the budget the attempts allow, so the release is not
    /// racing the last attempt. What the test discriminates is the first attempt failing and a
    /// later one succeeding, which is exactly what a single-attempt removal cannot do.</para>
    /// </summary>
    [Fact]
    public void RecoversAHoldThatIsReleasedWhileItWaits()
    {
        using var temp = new TempDirectory();
        var tree = temp.CreateDirectory("released");
        var handle = Hold(tree);

        using var release = new Timer(
            _ => handle.Dispose(), null, TimeSpan.FromMilliseconds(150), Timeout.InfiniteTimeSpan);

        var removed = ScratchTree.TryRemove(
            tree, new RemovalAttempts(Count: 40, Between: TimeSpan.FromMilliseconds(50)));

        Assert.True(removed);
        Assert.False(Directory.Exists(tree));
    }

    /// <summary>
    /// Open a file inside <paramref name="tree"/> in a way that refuses the delete.
    ///
    /// <para><see cref="FileShare.None"/> rather than a read share, because Windows allows a file to
    /// be deleted while it is open when the opener granted <see cref="FileShare.Delete"/>. Denying
    /// every share is what makes the refusal certain rather than likely.</para>
    /// </summary>
    private static FileStream Hold(string tree)
    {
        var file = Path.Combine(tree, "held.bin");
        File.WriteAllBytes(file, new byte[8]);

        return new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
    }
}
