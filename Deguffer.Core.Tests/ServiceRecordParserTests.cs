using Deguffer.Core.Memory;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The service list names the process hosting each service, with its strings as pointers into the
/// same buffer. These prove both widths read the right process, and that an entry or string outside
/// the buffer stops the read and says the list is incomplete. Every name here is invented.
/// </summary>
public sealed class ServiceRecordParserTests
{
    private static readonly RunningService Indexer = new("ExampleIndexer", "Example indexing service", 2_116);
    private static readonly RunningService Updater = new("ExampleUpdater", "Example update service", 3_380);

    [Theory]
    [InlineData(8)]
    [InlineData(4)]
    public void EachServiceIsReadWithItsHost(int pointerSize)
    {
        var buffer = new ServiceTableBuffer(pointerSize).Add(Indexer).Add(Updater);
        var services = new List<RunningService>();

        var complete = ServiceRecordParser.Parse(buffer.Build(), buffer.Base, 2, pointerSize, services);

        Assert.True(complete);
        Assert.Equal([Indexer, Updater], services);
    }

    [Fact]
    public void AServiceWithNoProcessIsLeftOut()
    {
        var stopping = Updater with { ProcessId = 0 };
        var buffer = new ServiceTableBuffer(8).Add(Indexer).Add(stopping);
        var services = new List<RunningService>();

        var complete = ServiceRecordParser.Parse(buffer.Build(), buffer.Base, 2, 8, services);

        Assert.True(complete);
        Assert.Equal([Indexer], services);
    }

    [Fact]
    public void MoreEntriesThanTheBufferHoldsAreRefused()
    {
        var buffer = new ServiceTableBuffer(8).Add(Indexer);
        var services = new List<RunningService>();

        var complete = ServiceRecordParser.Parse(buffer.Build(), buffer.Base, 1_000, 8, services);

        Assert.False(complete);
        Assert.Empty(services);
    }

    [Fact]
    public void ANamePointingPastTheBufferStopsTheRead()
    {
        var buffer = new ServiceTableBuffer(8).Add(Indexer).Add(Updater);
        var data = buffer.Build();
        var services = new List<RunningService>();

        buffer.WritePointer(data.AsSpan(buffer.EntrySize), 0, buffer.Base + (ulong)data.Length);

        var complete = ServiceRecordParser.Parse(data, buffer.Base, 2, 8, services);

        Assert.False(complete);
        Assert.Equal([Indexer], services);
    }

    /// <summary>
    /// The last string in the buffer with its terminator cut off: a read that took whatever was left
    /// would hand back a name nobody wrote.
    /// </summary>
    [Fact]
    public void ANameWithNoTerminatorStopsTheRead()
    {
        var buffer = new ServiceTableBuffer(8).Add(Indexer).Add(Updater);
        var data = buffer.Build();
        var services = new List<RunningService>();

        var complete = ServiceRecordParser.Parse(data.AsSpan(0, data.Length - 2), buffer.Base, 2, 8, services);

        Assert.False(complete);
        Assert.Equal([Indexer], services);
    }
}
