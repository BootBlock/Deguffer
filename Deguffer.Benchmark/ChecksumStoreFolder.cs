using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Benchmark;

/// <summary>
/// The folder the duplicates route keeps its checksums in: a new one in the temporary folder, named
/// for no run but its own, so the route never reads or writes the store the app keeps under
/// <c>%LOCALAPPDATA%\Deguffer</c>.
/// </summary>
internal sealed class ChecksumStoreFolder
{
    private ChecksumStoreFolder(string root) => Root = root;

    /// <summary>The folder, fully qualified. Never printed, for the reason <see cref="MeasuredPlace"/> gives.</summary>
    public string Root { get; }

    /// <summary>The store's file inside it.</summary>
    public string Store => Path.Join(Root, "checksums.bin");

    /// <summary>A new folder in <paramref name="environment"/>'s temporary folder.</summary>
    public static ChecksumStoreFolder Create(IUserEnvironment environment)
    {
        var folder = new ChecksumStoreFolder(Path.Join(environment.TempPath, $"Deguffer.Benchmark.{Guid.NewGuid():N}"));
        Directory.CreateDirectory(LongPath.Extended(folder.Root));

        return folder;
    }

    /// <summary>
    /// Remove the folder and the store in it, through the removal a clean uses. Whether all of it went.
    /// Never cancelled, because a folder left half removed is left for the person who ran this to find.
    /// </summary>
    public async Task<bool> RemoveAsync() => (await DirectoryRemover.RemoveAsync(Root).ConfigureAwait(false)).RootRemoved;
}
