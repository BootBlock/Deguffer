using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

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
    /// A declared root Windows will not describe does not read as a machine that has none.
    ///
    /// <para><see cref="DeclaredLocationScan.FoundNothing"/> is what eight providers turn into
    /// "there is nothing here", and a two-state existence check put a refusal on that branch — so
    /// the sentence denied a location the provider's own presence probe had already found. The
    /// refusal is neither a target nor a decline: Deguffer did not choose to leave it alone.</para>
    /// </summary>
    [Fact]
    public void ARootWindowsWillNotDescribeIsNotAMachineWithNothingOnIt()
    {
        _temp.CreateDirectory("root", "logs");

        using var denied = DeniedDirectory.WithUnreadableAttributes(Path.Combine(_temp.Path, "root"));

        var scan = DeclaredLocations.Examine(
            [Root(Path.Combine(_temp.Path, "root"), new DeclaredLocation("logs", "Servicing logs."))]);

        Assert.False(scan.FoundNothing);
        Assert.True(scan.CouldNotBeReached);
        Assert.Empty(scan.Targets);
        Assert.Empty(scan.Declined);
        Assert.Contains(scan.Notes, n => n.Severity == PlanNoteSeverity.Warning);

        // Protected like a decline. §5.6 records it as a refusal rather than as "nothing to
        // preserve", and checks it again after the run.
        Assert.Contains(
            scan.Protected,
            p => p.Path.Equals(Path.Combine(_temp.Path, "root"), StringComparison.OrdinalIgnoreCase)
                && p.Reason == UnreadableRoot.UnreachedReason);
    }

    /// <summary>
    /// Each way a declared directory is dated, with a folder whose own timestamps are old and whose
    /// child was written since: one level down leaves the age to the plan's bounded step, which reads
    /// the child, a folder dated by itself carries its own time and never the child's, and an
    /// unreported age carries nothing.
    /// </summary>
    [Fact]
    public void ADeclaredDirectoryIsDatedAsItsDeclarationSays()
    {
        var old = DateTime.UtcNow - TimeSpan.FromDays(30);
        var root = Path.Combine(_temp.Path, "root");

        foreach (var name in new[] { "below", "itself", "unreported" })
        {
            var folder = Path.Combine(root, name);
            Directory.CreateDirectory(Path.Combine(folder, "child"));
            Directory.SetLastWriteTimeUtc(Path.Combine(folder, "child"), DateTime.UtcNow);
            Directory.SetCreationTimeUtc(folder, old);
            Directory.SetLastWriteTimeUtc(folder, old);
        }

        var scan = DeclaredLocations.Examine(
        [
            Root(
                root,
                new DeclaredLocation("below", "Written inside.", Age: DeclaredLocationAge.OneLevelDown),
                new DeclaredLocation("itself", "Made whole once.", Age: DeclaredLocationAge.Itself),
                new DeclaredLocation("unreported", "Nested.", Age: DeclaredLocationAge.Unreported)),
        ]);

        var below = scan.Targets.Single(t => t.Path.EndsWith("below", StringComparison.Ordinal));
        var itself = scan.Targets.Single(t => t.Path.EndsWith("itself", StringComparison.Ordinal));
        var unreported = scan.Targets.Single(t => t.Path.EndsWith("unreported", StringComparison.Ordinal));

        Assert.True(below.DatedOneLevelDown);
        Assert.Null(below.LastWritten);

        Assert.False(itself.DatedOneLevelDown);
        Assert.NotNull(itself.LastWritten);
        Assert.True((itself.LastWritten.Value - old).Duration() < TimeSpan.FromSeconds(1), $"{itself.LastWritten:O}");

        Assert.False(unreported.DatedOneLevelDown);
        Assert.Null(unreported.LastWritten);
    }

    /// <summary>
    /// A relative path written with a forward slash. Windows accepts both separators and
    /// <see cref="Path.Combine"/> resolves either, so a declaration written as <c>Logs/CBS</c> works
    /// — and splitting on the backslash alone would see one segment, skip the ancestor walk
    /// entirely, and step straight through a linked <c>Logs</c> into a tree nobody classified.
    /// That is the §5.2 failure this walk exists to prevent, arriving through a typing convention.
    /// </summary>
    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public void AForwardSlashInADeclarationStillWalksTheAncestors(DirectoryLinkKind kind)
    {
        var root = _temp.CreateDirectory("root");
        var outside = _temp.CreateDirectory("elsewhere", "CBS");
        File.WriteAllBytes(Path.Combine(outside, "irreplaceable.log"), new byte[4096]);

        DirectoryLink.Create(kind, Path.Combine(root, "Logs"), Path.Combine(_temp.Path, "elsewhere"));

        var scan = DeclaredLocations.Examine(
            [Root(root, new DeclaredLocation("Logs/CBS", "Servicing logs."))]);

        Assert.Empty(scan.Targets);
        Assert.Contains(Path.Combine(root, "Logs"), scan.Declined, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(scan.Notes, n => n.Message.Contains("link", StringComparison.Ordinal));
    }

    /// <summary>
    /// A linked container shared by two declarations is declined once. Each declaration walks
    /// the whole chain down from the root, so the container is met once per declaration, and one
    /// folder described twice reads to the user as two folders.
    /// </summary>
    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public void AContainerSharedByTwoDeclarationsIsDeclinedOnce(DirectoryLinkKind kind)
    {
        var root = _temp.CreateDirectory("root");

        DirectoryLink.Create(
            kind, Path.Combine(root, "Logs"), _temp.CreateDirectory("elsewhere"));

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
