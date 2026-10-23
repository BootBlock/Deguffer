using Deguffer.Core.Startup;

namespace Deguffer.Testing;

/// <summary>Packaged startup tasks as a test describes them.</summary>
public sealed class FakePackagedStartupTasks(PackagedTasksRead read) : IPackagedStartupTasks
{
    public static FakePackagedStartupTasks None { get; } = new(new PackagedTasksRead(true, []));

    public PackagedTasksRead Read(CancellationToken ct) => read;
}
