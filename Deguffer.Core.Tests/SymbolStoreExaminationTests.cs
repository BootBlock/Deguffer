using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The look into a symbol store that both debugger rows and Explore's refusals are built from. Its entries
/// are looked inside several at a time, and what it reports has to read as the listings found it.
/// </summary>
public sealed class SymbolStoreExaminationTests : IDisposable
{
    private const string PdbIndex = "3844DBB920174967BE7AA4A2C20430FA2";

    private readonly TempDirectory _temp = new();
    private readonly FakeVolumeInventory _volumes = new();

    public void Dispose() => _temp.Dispose();

    private string Store => Path.Combine(_temp.Path, "Symbols");

    private static void WriteFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[16]);
    }

    /// <summary>
    /// The first entry in the listing holds hundreds of index folders and the rest one each, so the later
    /// entries are recognised before the first is. The entries and the survivors are still reported in
    /// the order the listings found them, in a flat store and a two-level one.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReportsEntriesAndSurvivorsInListingOrderHoweverTheLooksFinish(bool twoLevel)
    {
        if (twoLevel)
        {
            WriteFile(Path.Combine(Store, SymbolStoreLayout.TwoLevelFile));
        }

        var parent = twoLevel ? Path.Combine(Store, "mo") : Store;

        for (var i = 0; i < 80; i++)
        {
            var name = $"module{i:D2}.pdb";
            var entry = Path.Combine(parent, name);

            if (i % 4 == 1)
            {
                WriteFile(Path.Combine(entry, "latest", name));
                continue;
            }

            var indexes = i == 0 ? 500 : 1;
            for (var index = 0; index < indexes; index++)
            {
                WriteFile(Path.Combine(entry, $"{index:X9}", name));
            }
        }

        string[] listedFolders = [.. FolderEntries.Of(parent)!.OfType<DirectoryInfo>().Select(folder => LongPath.Display(folder.FullName))];
        string[] offered = [.. listedFolders.Where(folder => !Directory.Exists(Path.Combine(folder, "latest")))];
        string[] unrecognised = [.. listedFolders.Where(folder => Directory.Exists(Path.Combine(folder, "latest")))];
        string[] storeLevel = twoLevel ? [.. FolderEntries.Of(Store)!.Select(item => LongPath.Display(item.FullName))] : [];

        var store = new SymbolStore(Store, SymbolStoreFeed.Microsoft, [], ["a test"]);
        var examination = SymbolStoreExamination.Of([store], [store], _volumes, CancellationToken.None);

        Assert.Equal(offered, examination.Entries.Select(entry => entry.Path));
        Assert.Equal([Store, .. storeLevel, .. unrecognised], examination.Survivors.Select(survivor => survivor.Path));
        Assert.All(examination.Entries, entry => Assert.Equal(parent, entry.Parent));
    }
}
