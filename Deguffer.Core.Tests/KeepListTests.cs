using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;

namespace Deguffer.Core.Tests;

/// <summary>The keep list's own rules, apart from any plan or file.</summary>
public sealed class KeepListTests
{
    private static KeptItem Item(string provider, string key, string? name = null) =>
        new(provider, provider, new ItemIdentity(key, name ?? key));

    /// <summary>
    /// Most keys are directory names, and NTFS does not tell <c>Chromium-1228</c> from
    /// <c>chromium-1228</c>. Matching by case would stop protecting an item a tool happened to
    /// recreate in different case, and that is the direction a keep list must not fail in.
    /// </summary>
    [Fact]
    public void MatchesAKeyWithoutRegardToCase()
    {
        var list = new KeepList([Item("playwright", "chromium-1228")]);

        Assert.Contains("CHROMIUM-1228", list.KeysFor("playwright"));
    }

    /// <summary>A key is unique within one provider and means nothing to another.</summary>
    [Fact]
    public void KeepsOneProvidersKeysAwayFromAnother()
    {
        var list = new KeepList([Item("playwright", "4.18.1")]);

        Assert.Empty(list.KeysFor("azure-functions-tools"));
        Assert.Empty(list.KeysFor("unknown"));
    }

    /// <summary>
    /// Keeping an item twice holds it once, under the later name, so a list of kept items never shows
    /// the same thing twice.
    /// </summary>
    [Fact]
    public void KeepingAnItemAgainReplacesItRatherThanAddingASecond()
    {
        var list = new KeepList([Item("squirrel", "Chatterbox/app-3.6.3", "Chatterbox 3.6.3")])
            .With(Item("squirrel", "chatterbox/APP-3.6.3", "Chatterbox 3.6.3 (renamed)"));

        var kept = Assert.Single(list.Items);
        Assert.Equal("Chatterbox 3.6.3 (renamed)", kept.Item.Name);
    }

    /// <summary>Releasing one item leaves every other entry, in every provider, exactly where it was.</summary>
    [Fact]
    public void ReleasingAnItemTouchesNothingElse()
    {
        var list = new KeepList(
        [
            Item("playwright", "chromium-1228"),
            Item("playwright", "firefox-1532"),
            Item("azure-functions-tools", "chromium-1228"),
        ]);

        var released = list.Without("playwright", "CHROMIUM-1228");

        Assert.Equal(["firefox-1532"], released.KeysFor("playwright"));
        Assert.Contains("chromium-1228", released.KeysFor("azure-functions-tools"));

        // Immutable: the list the change was made from still keeps what it kept.
        Assert.Contains("chromium-1228", list.KeysFor("playwright"));
    }
}
