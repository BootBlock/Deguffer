using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Removing one scratch tree when something else is holding a file inside it open, or when a test
/// left something inside it read-only.
///
/// <para>The suite forgives a refused delete rather than fail a run over it, so a refusal that a
/// second attempt would have cleared used to strand the whole tree for good. These cases pin the
/// two halves of that: the wait recovers a transient hold, and a hold that outlasts the attempts is
/// still forgiven rather than thrown.</para>
///
/// <para>A read-only entry is a refusal no wait recovers. The read-only cases make one attempt,
/// which is what the sweep makes, so a removal that cleared the bit only before a later attempt
/// fails them too.</para>
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

    [Fact]
    public void RemovesATreeHoldingAReadOnlyFile()
    {
        using var temp = new TempDirectory();
        var tree = temp.CreateDirectory("read-only file");
        var file = Path.Combine(tree, "locked.bin");
        File.WriteAllBytes(file, new byte[8]);
        File.SetAttributes(file, FileAttributes.ReadOnly);

        Assert.True(ScratchTree.TryRemove(tree, RemovalAttempts.One));
        Assert.False(Directory.Exists(tree));
    }

    /// <summary>
    /// A hidden, system, read-only file, which a listing on the default options skips. Clearing only
    /// what such a listing shows would leave this one refusing the delete.
    /// </summary>
    [Fact]
    public void RemovesAReadOnlyFileTheDefaultListingHides()
    {
        using var temp = new TempDirectory();
        var tree = temp.CreateDirectory("hidden");
        var file = Path.Combine(tree, "desktop.ini");
        File.WriteAllBytes(file, new byte[8]);
        File.SetAttributes(file, FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);

        Assert.True(ScratchTree.TryRemove(tree, RemovalAttempts.One));
        Assert.False(Directory.Exists(tree));
    }

    /// <summary>
    /// Read-only directories, the tree's own directory and one a level down, which Windows refuses to
    /// remove exactly as it refuses a read-only file.
    /// </summary>
    [Fact]
    public void RemovesReadOnlyDirectoriesTheTreeItselfIncluded()
    {
        using var temp = new TempDirectory();
        var tree = temp.CreateDirectory("read-only directories");
        var inner = temp.CreateDirectory("read-only directories", "inner", "deeper");
        File.WriteAllBytes(Path.Combine(inner, "content.bin"), new byte[8]);
        File.SetAttributes(inner, File.GetAttributes(inner) | FileAttributes.ReadOnly);
        File.SetAttributes(tree, File.GetAttributes(tree) | FileAttributes.ReadOnly);

        Assert.True(ScratchTree.TryRemove(tree, RemovalAttempts.One));
        Assert.False(Directory.Exists(tree));
    }

    /// <summary>
    /// A read-only link goes, and what it points at outside the tree keeps every read-only bit it
    /// had. The link's own bit is cleared, and the walk does not go through it: the far side is a
    /// directory no test handed to this removal.
    ///
    /// <para>This case alone takes the backoff. The runtime's recursive delete reports a junction as
    /// refused even when it removed it, so a tree holding one is gone only on a later attempt,
    /// read-only bit or not. A read-only link refuses every attempt until its bit is cleared, so the
    /// case still fails without the clearing.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public void RemovesAReadOnlyLinkWithoutChangingItsTarget(DirectoryLinkKind kind)
    {
        using var temp = new TempDirectory();
        var tree = temp.CreateDirectory("tree");
        var outside = temp.CreateDirectory("outside");
        var bystander = Path.Combine(outside, "bystander.bin");
        File.WriteAllBytes(bystander, new byte[8]);
        File.SetAttributes(bystander, FileAttributes.ReadOnly);
        File.SetAttributes(outside, File.GetAttributes(outside) | FileAttributes.ReadOnly);

        var link = Path.Combine(tree, "link");
        DirectoryLink.Create(kind, link, outside);
        File.SetAttributes(link, File.GetAttributes(link) | FileAttributes.ReadOnly);

        Assert.True(ScratchTree.TryRemove(tree, RemovalAttempts.WithBackoff));
        Assert.False(Directory.Exists(tree));

        Assert.True(File.Exists(bystander), "the removal followed the link out of the tree");
        Assert.True(File.GetAttributes(bystander).HasFlag(FileAttributes.ReadOnly), "a file beyond the link was changed");
        Assert.True(File.GetAttributes(outside).HasFlag(FileAttributes.ReadOnly), "the link's target was changed");
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
