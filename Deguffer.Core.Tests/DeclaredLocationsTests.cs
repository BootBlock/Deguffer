using Deguffer.Core.Providers;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The declared-path resolver on its own, for the properties no provider's own table can exercise.
///
/// Everything the two providers declare today is built with <see cref="Path.Combine"/> and is a
/// directory, so the cases below are about what happens when a future declaration is not — which is
/// exactly where a §5.2 rule fails quietly rather than loudly.
/// </summary>
public sealed class DeclaredLocationsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static DeclaredRoot Root(string path, params DeclaredLocation[] locations) =>
        new(path, "The root must survive.", RequiresElevation: false, locations, []);

    /// <summary>
    /// A relative path written with a forward slash. Windows accepts both separators and
    /// <see cref="Path.Combine"/> resolves either, so a declaration written as <c>Logs/CBS</c> works
    /// — and splitting on the backslash alone would see one segment, skip the ancestor walk
    /// entirely, and step straight through a junctioned <c>Logs</c> into a tree nobody classified.
    /// That is the §5.2 failure this walk exists to prevent, arriving through a typing convention.
    /// </summary>
    [Fact]
    public void AForwardSlashInADeclarationStillWalksTheAncestors()
    {
        var root = _temp.CreateDirectory("root");
        var outside = _temp.CreateDirectory("elsewhere", "CBS");
        File.WriteAllBytes(Path.Combine(outside, "irreplaceable.log"), new byte[4096]);

        Directory.CreateSymbolicLink(Path.Combine(root, "Logs"), Path.Combine(_temp.Path, "elsewhere"));

        var scan = DeclaredLocations.Examine(
            [Root(root, new DeclaredLocation("Logs/CBS", "Servicing logs."))]);

        Assert.Empty(scan.Targets);
        Assert.Contains(Path.Combine(root, "Logs"), scan.Declined, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(scan.Notes, n => n.Message.Contains("link", StringComparison.Ordinal));
    }

    /// <summary>
    /// A junctioned container shared by two declarations is declined once. Each declaration walks
    /// the whole chain down from the root, so the container is met once per declaration, and one
    /// folder described twice reads to the user as two folders.
    /// </summary>
    [Fact]
    public void AContainerSharedByTwoDeclarationsIsDeclinedOnce()
    {
        var root = _temp.CreateDirectory("root");

        Directory.CreateSymbolicLink(
            Path.Combine(root, "Logs"), _temp.CreateDirectory("elsewhere"));

        var scan = DeclaredLocations.Examine(
        [
            Root(
                root,
                new DeclaredLocation(Path.Combine("Logs", "CBS"), "Servicing logs."),
                new DeclaredLocation(Path.Combine("Logs", "WindowsUpdate"), "Update logs.")),
        ]);

        Assert.Single(scan.Declined);
        Assert.Single(scan.Notes);
    }

    /// <summary>
    /// A declaration whose target is not there produces no protection for the containers above it.
    /// Naming them would be true and would say nothing: §5.6's report is about what survived a
    /// removal, and there was no removal to survive.
    /// </summary>
    [Fact]
    public void ContainersAreNamedOnlyWhereSomethingInsideThemIsActuallyTargeted()
    {
        var root = _temp.CreateDirectory("root");
        _temp.CreateDirectory("root", "Logs");

        var scan = DeclaredLocations.Examine(
            [Root(root, new DeclaredLocation(Path.Combine("Logs", "CBS"), "Servicing logs."))]);

        Assert.Empty(scan.Targets);
        Assert.DoesNotContain(
            scan.Protected, p => p.Path.Equals(Path.Combine(root, "Logs"), StringComparison.OrdinalIgnoreCase));
    }
}
