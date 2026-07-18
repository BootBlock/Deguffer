using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>§5.2 — the fail-closed rule, tested at the level where it is decided.</summary>
public class DisposableChildSetTests
{
    private static readonly DisposableChildSet Subject = new(
    [
        new ChildClassification("caches", SafetyTier.RegenerableCache, "Rebuilt on demand."),
    ]);

    [Fact]
    public void RecognisedChildKeepsItsDeclaredTier()
    {
        var classification = Subject.Classify("caches");

        Assert.Equal(SafetyTier.RegenerableCache, classification.Tier);
        Assert.True(Subject.IsDisposable("caches"));
    }

    [Fact]
    public void MatchingIsCaseInsensitiveBecauseWindowsIs() =>
        Assert.Equal(SafetyTier.RegenerableCache, Subject.Classify("CACHES").Tier);

    [Theory]
    [InlineData("gradle.properties")]
    [InlineData("init.d")]
    [InlineData("something-a-future-gradle-version-invented")]
    [InlineData("caches.bak")]
    public void UnrecognisedChildIsTier4(string name)
    {
        var classification = Subject.Classify(name);

        Assert.Equal(SafetyTier.DoNotTouch, classification.Tier);
        Assert.False(Subject.IsDisposable(name));
    }

    [Fact]
    public void UnrecognisedChildIsNotMatchedByPrefixOrSubstring()
    {
        // A name-shaped heuristic is exactly the mistake §5.2 exists to prevent: "caches" being
        // disposable must say nothing about "caches-config".
        Assert.Equal(SafetyTier.DoNotTouch, Subject.Classify("caches-config").Tier);
        Assert.Equal(SafetyTier.DoNotTouch, Subject.Classify("my-caches").Tier);
    }
}
