using Deguffer.Core.Health;
using Deguffer.Testing;
using Microsoft.Win32;

namespace Deguffer.Core.Tests;

/// <summary>
/// #751 through the whole reader: an installed drive database names a SATA drive's family as one of its
/// details, is looked at once for a whole read, and is never looked at by a machine with no ATA drive.
/// </summary>
public sealed class DriveDatabaseReaderTests : IDisposable
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\smartmontools";
    private const string FactLabel = "smartmontools drive database";

    private readonly FakeDriveProbe _probe = new();
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public DriveDatabaseReaderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private void Install(string text)
    {
        var install = Path.Combine(_temp.Path, "smartmontools");
        Directory.CreateDirectory(Path.Combine(install, "bin"));
        File.WriteAllText(Path.Combine(install, "bin", "drivedb.h"), text);
        _environment.WithMachineRegistryValue(UninstallKey, "InstallLocation", install, RegistryView.Registry64);
    }

    private void Sata(int disk, string model, params AtaEntry[] entries) =>
        _probe
            .Answer(disk, DriveQuestion.Device, DriveBytes.Device(DriveBytes.Sata, model, serial: $"SATA{disk}"))
            .Answer(disk, DriveQuestion.SeekPenalty, DriveBytes.SeekPenalty(false))
            .Answer(disk, DriveQuestion.PredictFailure, DriveBytes.Prediction(false, entries));

    private async Task<IReadOnlyList<DriveHealth>> ReadAll()
    {
        var reader = new DriveHealthReader(_probe, new FakeVolumeInventory(), new FakeStorageQueries(), _environment, TimeSpan.FromSeconds(30));
        var collected = new List<DriveHealth>();

        await reader.ReadAsync(reader.List()!, new Collect<DriveHealth>(collected), new Collect<DriveVolumes>([]), CancellationToken.None);
        return [.. collected.OrderBy(report => report.Name, StringComparer.Ordinal)];
    }

    [Fact]
    public async Task ASataDriveNamesItsFamilyAndTheReadingsTheDatabaseLaysOut()
    {
        Install(InventedDriveDatabase.Text);
        Sata(0, InventedDriveDatabase.Model, new AtaEntry(230, 100, 0x000A_0000_0007UL));

        var report = Assert.Single(await ReadAll());

        Assert.Contains(report.Facts, fact => fact is { Label: FactLabel, Value: InventedDriveDatabase.Family });
        var row = Assert.Single(report.Attributes, attribute => attribute.Key == "ata-230");
        Assert.Equal(("Example Paired Count", "10 / 7", ReadingBasis.DriveDatabase), (row.Name, row.Reading, row.Basis));
    }

    [Fact]
    public async Task ADriveNoEntryNamesSaysSo()
    {
        Install(InventedDriveDatabase.Text);
        Sata(0, "OTHERCO DRIVE 1TB", new AtaEntry(230, 100));

        var report = Assert.Single(await ReadAll());

        Assert.Contains(report.Facts, fact => fact is { Label: FactLabel, Value: "No entry for this drive" });
        Assert.All(report.Attributes, attribute => Assert.Equal(ReadingBasis.Deguffer, attribute.Basis));
    }

    [Fact]
    public async Task ADatabaseThatCannotBeReadIsNamedAndNoneOfItIsUsed()
    {
        Install(InventedDriveDatabase.Text + "\n{ broken");
        Sata(0, InventedDriveDatabase.Model, new AtaEntry(230, 100));

        var report = Assert.Single(await ReadAll());

        var fact = Assert.Single(report.Facts, fact => fact.Label == FactLabel);
        Assert.StartsWith("drivedb.h could not be read at line", fact.Value, StringComparison.Ordinal);
        Assert.All(report.Attributes, attribute => Assert.Equal(ReadingBasis.Deguffer, attribute.Basis));
    }

    [Fact]
    public async Task AMachineWithoutSmartmontoolsShowsNothingAboutIt()
    {
        Sata(0, InventedDriveDatabase.Model, new AtaEntry(230, 100));

        var report = Assert.Single(await ReadAll());

        Assert.DoesNotContain(report.Facts, fact => fact.Label == FactLabel);
    }

    [Fact]
    public async Task TheDatabaseIsLookedAtOnceForAWholeRead()
    {
        Install(InventedDriveDatabase.Text);
        Sata(0, InventedDriveDatabase.Model);
        Sata(1, InventedDriveDatabase.Model);
        Sata(2, "OTHERCO DRIVE 1TB");

        Assert.Equal(3, (await ReadAll()).Count(report => report.Facts.Any(fact => fact.Label == FactLabel)));
        Assert.Equal(1, _environment.RegistryReads);
    }

    [Fact]
    public async Task AMachineWithNoAtaDriveNeverLooksForTheDatabase()
    {
        Install(InventedDriveDatabase.Text);
        _probe
            .Answer(0, DriveQuestion.Device, DriveBytes.Device(DriveBytes.Nvme, "Test NVMe", serial: "NVME0"))
            .Answer(0, DriveQuestion.NvmeHealth, DriveBytes.NvmeHealth());

        Assert.Single(await ReadAll());
        Assert.Equal(0, _environment.RegistryReads);
    }

    /// <summary>Collects reports in the order they came. The reader reports from several threads, so each add is locked.</summary>
    private sealed class Collect<T>(List<T> into) : IProgress<T>
    {
        public void Report(T value)
        {
            lock (into)
            {
                into.Add(value);
            }
        }
    }
}
