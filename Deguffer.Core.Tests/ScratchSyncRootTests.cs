using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a disconnected <see cref="ScratchSyncRoot"/> shows this process, which every test of what
/// Deguffer's own calls see of a real placeholder relies on.
/// </summary>
[Collection(Collection)]
public sealed class ScratchSyncRootTests : IDisposable
{
    /// <summary>The collection every test class that makes a <see cref="ScratchSyncRoot"/> belongs to.</summary>
    public const string Collection = "Scratch sync roots";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// Windows disguises a placeholder only from a process connected to no sync root at all, so a
    /// root connected after another was disconnected undoes what the disconnect was for. A guard on
    /// <see cref="ScratchSyncRoot.Disconnect"/> cannot see a root connected later, which is why the
    /// test classes that make roots run one after another. The last read proves the view comes back.
    /// </summary>
    [Fact]
    public void AnyConnectedRootShowsThisProcessEveryPlaceholderUndisguised()
    {
        using var synced = new ScratchSyncRoot(_temp.CreateDirectory("Synced"));
        var online = synced.OnlineOnly("film.mkv", 5_000_000);
        synced.Disconnect();

        Assert.False(IsUndisguised(online), "The placeholder was not disguised with no root connected.");

        using (new ScratchSyncRoot(_temp.CreateDirectory("Other")))
        {
            Assert.True(IsUndisguised(online), "A root connected elsewhere left the placeholder disguised.");
        }

        Assert.False(IsUndisguised(online), "The placeholder stayed undisguised once no root was connected.");
    }

    /// <summary>
    /// Disconnecting while another root of this process is connected would hand the test the sync
    /// app's view, so it is refused and the root stays connected, which its placeholder shows by
    /// staying undisguised once the other root is gone. The same disconnect then goes through.
    /// </summary>
    [Fact]
    public void RefusesToDisconnectWhileAnotherRootIsConnected()
    {
        using var synced = new ScratchSyncRoot(_temp.CreateDirectory("Synced"));
        var online = synced.OnlineOnly("film.mkv", 5_000_000);

        using (new ScratchSyncRoot(_temp.CreateDirectory("Other")))
        {
            Assert.Throws<InvalidOperationException>(synced.Disconnect);
        }

        Assert.True(IsUndisguised(online), "The refused disconnect disconnected the root anyway.");

        synced.Disconnect();

        Assert.False(IsUndisguised(online), "The placeholder stayed undisguised once the root was disconnected.");
    }

    private static bool IsUndisguised(string path) =>
        FileInformation.Default.Describe(path, IdentityRoute.FileId).Description!.Attributes.HasFlag(FileAttributes.ReparsePoint);
}

/// <summary>
/// Runs the test classes that make a <see cref="ScratchSyncRoot"/> one after another, because a root
/// one of them has connected changes what the others see of their placeholders.
/// </summary>
[CollectionDefinition(ScratchSyncRootTests.Collection)]
public sealed class ScratchSyncRootsRunOneAtATime
{
}
