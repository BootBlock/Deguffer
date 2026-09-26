using System.Text.RegularExpressions;
using Deguffer.Testing;
using Xunit.Sdk;

namespace Deguffer.Core.Tests;

/// <summary>
/// The link fixture refuses a link a test could not follow, and says why, before any test builds on
/// it — and every link the suite makes goes through it.
/// </summary>
public sealed partial class SymbolicLinkTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ADirectoryLinkThatResolvesLeadsToItsTarget()
    {
        var target = _temp.CreateDirectory("target");
        File.WriteAllBytes(Path.Combine(target, "child.bin"), new byte[16]);
        var link = Path.Combine(_temp.Path, "link");

        SymbolicLink.ToDirectory(link, target);

        Assert.Equal(16, new FileInfo(Path.Combine(link, "child.bin")).Length);
    }

    [Fact]
    public void AFileLinkThatResolvesLeadsToItsTarget()
    {
        var target = _temp.CreateFile(16, "target.bin");
        var link = Path.Combine(_temp.Path, "link.bin");

        SymbolicLink.ToFile(link, target);

        Assert.Equal(16, File.ReadAllBytes(link).Length);
    }

    /// <summary>
    /// A link to itself is a refusal every machine produces, and the probe raises it the way it
    /// raises an untrusted mount point: as an <see cref="IOException"/> from following the link.
    /// </summary>
    [Fact]
    public void ALinkTheSystemWillNotFollowFailsQuotingTheSystemsOwnMessage()
    {
        var link = Path.Combine(_temp.Path, "loop");

        var failure = Assert.Throws<InvalidOperationException>(() => SymbolicLink.ToDirectory(link, link));

        var refusal = Assert.IsType<IOException>(failure.InnerException);
        Assert.Contains(refusal.Message, failure.Message, StringComparison.Ordinal);
        Assert.Contains(link, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADirectoryLinkToNothingFailsNamingWhereItLeads()
    {
        var target = Path.Combine(_temp.Path, "missing");

        var failure = Assert.Throws<TrueException>(
            () => SymbolicLink.ToDirectory(Path.Combine(_temp.Path, "link"), target));

        Assert.Contains(target, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileLinkToNothingFailsNamingWhereItLeads()
    {
        var target = Path.Combine(_temp.Path, "missing.bin");

        var failure = Assert.Throws<TrueException>(
            () => SymbolicLink.ToFile(Path.Combine(_temp.Path, "link.bin"), target));

        Assert.Contains(target, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The fixture only helps where it is used, and a link made directly fails the old way: a bare
    /// assertion against production code that is correct. The fixture's own file is expected to
    /// match, which also proves the sweep still reads the source.
    ///
    /// <para>Every project that holds tests or their fixtures is swept, since a link made in one of
    /// the App's tests fails the same way.</para>
    /// </summary>
    [Fact]
    public void NoTestCreatesALinkWithoutTheFixture()
    {
        string[] projects = ["Deguffer.Core.Tests", "Deguffer.App.Tests", "Deguffer.Testing"];

        var creating = projects
            .SelectMany(project => Directory.EnumerateFiles(
                Path.Combine(MarkdownGuide.RepositoryRoot, project), "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => LinkCreation().IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(MarkdownGuide.RepositoryRoot, file));

        Assert.Equal([Path.Combine("Deguffer.Testing", "SymbolicLink.cs")], creating);
    }

    [GeneratedRegex(@"\bCreateSymbolicLink\s*\(|\bCreateAsSymbolicLink\s*\(")]
    private static partial Regex LinkCreation();
}
