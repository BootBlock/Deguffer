using Deguffer.Core.Providers;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// A row that owns the entries of each temporary folder carrying one of the names it was given, and
/// records the folders it was asked about.
/// </summary>
public sealed class FakeTemporaryFolderTenant(string name, params string[] entryNames) : ITemporaryFolderTenant
{
    public string Name { get; } = name;

    public IReadOnlyList<string> AskedAbout { get; private set; } = [];

    public int InvalidateCount { get; private set; }

    public void InvalidateCaches() => InvalidateCount++;

    public Task<IReadOnlyList<string>> ClaimedEntriesAsync(
        IReadOnlyList<string> folders,
        CancellationToken ct = default)
    {
        AskedAbout = folders;

        return Task.FromResult<IReadOnlyList<string>>(
        [
            .. from folder in folders
               from entry in entryNames
               let path = Path.Combine(folder, entry)
               where Directory.Exists(path) || File.Exists(path)
               select path,
        ]);
    }
}
