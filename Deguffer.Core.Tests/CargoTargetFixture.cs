using Deguffer.Core.Configuration;
using Deguffer.Core.Providers;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A source root holding Cargo projects, and the two Rust rows that read the <c>target</c> inside each,
/// for the tests of what those rows offer from it.
/// </summary>
internal sealed class CargoTargetFixture : IDisposable
{
    /// <summary>The tag Cargo writes into every target directory it creates.</summary>
    public const string CargoTag =
        "Signature: 8a477f597d28d172789f06886806bc55\n# This file is a cache directory tag created by cargo.\n"
        + "# For information about cache directory tags see https://bford.info/cachedir/\n";

    private readonly FakeUserEnvironment _environment;
    private readonly SourceRootStore _roots;

    public CargoTargetFixture()
    {
        _environment = new FakeUserEnvironment(Temp.Path);
        _roots = new SourceRootStore(_environment);
    }

    public TempDirectory Temp { get; } = new();

    public void Dispose() => Temp.Dispose();

    /// <summary>
    /// A Cargo project under the source root, returning its <c>target</c>, which holds Cargo's tag and a
    /// <c>debug</c> profile in the layout Cargo wrote before 1.100.
    /// </summary>
    public string CargoProject(string name, bool writeIncremental = false)
    {
        var root = Temp.CreateDirectory("src");
        _roots.Save([new SourceRoot(root)]);

        var target = BuildDirectoryFixture.CreateCargoProject(Path.Combine(root, name), writeIncremental: writeIncremental);
        File.WriteAllText(Path.Combine(target, "CACHEDIR.TAG"), CargoTag);
        return target;
    }

    public CargoTargetProvider BuildOutput(CargoTargetSurvey? survey = null)
    {
        var scanner = new FakeDirectoryScanner();

        return new CargoTargetProvider(
            _roots, new SourceDirectoryDiscovery(scanner, new FakeVolumeInventory()), FakeLiveTreeInspector.NothingLive,
            _environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, scanner, targets: survey);
    }

    public CargoIncrementalProvider Incremental(CargoTargetSurvey? survey = null)
    {
        var scanner = new FakeDirectoryScanner();

        return new CargoIncrementalProvider(
            _roots, new SourceDirectoryDiscovery(scanner, new FakeVolumeInventory()), FakeLiveTreeInspector.NothingLive,
            _environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, scanner, targets: survey);
    }
}
