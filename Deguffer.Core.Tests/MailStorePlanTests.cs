using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// §9 applied to a finished plan: which steps stay and do less, which are withheld because they
/// cannot leave a store behind, and the protection that proves each store survived the run.
///
/// <para>Driven against plans built here rather than through a provider, because the rule is the
/// plan's and not any provider's. <see cref="CleanupProviderBaseTests"/> covers that every provider's
/// plan goes through it.</para>
/// </summary>
public sealed class MailStorePlanTests
{
    private const string Archive = @"C:\Users\testuser\AppData\Local\Temp\Temp1_mail.zip\archive.pst";

    private const string Mailbox = @"C:\Users\testuser\AppData\Local\npm-cache\someone@example.com.ost";

    private static CleanupPlan Planning(params CleanupStep[] steps) => new()
    {
        ProviderId = "test",
        ProviderName = "Test",
        Tier = SafetyTier.RegenerableCache,
        WhatHappensOnNextUse = "Nothing.",
        Steps = steps,
    };

    [Fact]
    public void APlanHoldingNoStoreIsReturnedAsItIs()
    {
        var plan = Planning(new DeleteDirectoryStep(@"C:\Users\testuser\.gradle\caches", "Caches"));

        Assert.Same(plan, MailStorePlan.Apply(plan));
    }

    /// <summary>
    /// A removal Deguffer performs itself steps over a store, so its step stays and does less. What
    /// the plan adds is the evidence: each store protected by its path, so a run that took one fails
    /// its verification, and a sentence saying so before anything runs.
    /// </summary>
    [Fact]
    public void ARemovalDegufferPerformsStaysAndEveryStoreInItIsProtected()
    {
        var clear = new ClearDirectoryStep(@"C:\Users\testuser\AppData\Local\Temp", "Temporary files")
        {
            Estimated = new ScanSize(4096, 4096),
            MailStores = [Archive],
        };

        var applied = MailStorePlan.Apply(Planning(clear));

        Assert.Equal([clear], applied.Steps);

        var protection = Assert.Single(applied.ProtectedPaths);
        Assert.Equal(Archive, protection.Path);
        Assert.Equal(Withholding.MailStore, protection.Withheld);
        Assert.True(protection.ExistedBefore);
        Assert.False(protection.HeldContentBefore);

        Assert.True(applied.HoldsMailStores);
        Assert.True(applied.HasSomethingToProve);
        Assert.Contains(applied.Notes, n => n.Message.Contains("Outlook data file", StringComparison.Ordinal));

        // Named, so the reader can find which file and where before anything runs.
        Assert.Contains(applied.Notes, n => n.Message.Contains(Archive, StringComparison.Ordinal));
    }

    /// <summary>
    /// A removal whose subject goes whole or not at all cannot step over a store, so it is withheld
    /// like a bin Windows empties — and the subject it would have removed is protected as well as the
    /// store, so §5.6 sees an over-broad rule elsewhere in the run empty it.
    /// </summary>
    [Fact]
    public void AnIndivisibleRemovalHoldingAStoreIsWithheldAndItsSubjectProtected()
    {
        const string bin = @"D:\$Recycle.Bin\S-1-5-21-1111111111-2222222222-3333333333-1001";
        var store = bin + @"\$RDEF456\mail\archive.pst";

        var whole = new DeleteDirectoryStep(bin, "A bin") { IsIndivisible = true, MailStores = [store] };

        var applied = MailStorePlan.Apply(Planning(whole));

        Assert.Empty(applied.Steps);
        Assert.Contains(applied.ProtectedPaths, p => p.Path == store && p.Withheld == Withholding.MailStore);
        Assert.Contains(applied.ProtectedPaths, p => p.Path == bin);
        Assert.Contains(applied.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(store, StringComparison.Ordinal));
    }

    /// <summary>
    /// A tool's own command decides what it removes and cannot be told to leave one file, so a store
    /// inside what it clears withholds it. Another command in the same plan, with no store in its
    /// reach, is untouched.
    /// </summary>
    [Fact]
    public void AToolsOwnCommandIsWithheldWhenAStoreIsInsideWhatItClears()
    {
        var npm = new RunCommandStep("npm.cmd", "cache clean --force", "Clear the npm cache")
        {
            MeasuredPaths = [@"C:\Users\testuser\AppData\Local\npm-cache"],
            MailStores = [Mailbox],
        };

        var go = new RunCommandStep("go.exe", "clean -cache", "Clear the Go build cache")
        {
            MeasuredPaths = [@"C:\Users\testuser\AppData\Local\go-build"],
        };

        var applied = MailStorePlan.Apply(Planning(npm, go));

        Assert.Equal([go], applied.Steps);
        Assert.Equal(Mailbox, Assert.Single(applied.ProtectedPaths).Path);
        Assert.Contains(applied.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning
            && n.Message.Contains(Mailbox, StringComparison.Ordinal));
    }

    [Fact]
    public void AFileStepNamingAStoreIsWithheld()
    {
        var file = new DeleteFileStep(Archive, "A file") { MailStores = [Archive] };

        var applied = MailStorePlan.Apply(Planning(file));

        Assert.Empty(applied.Steps);
        Assert.Equal(Archive, Assert.Single(applied.ProtectedPaths).Path);
        Assert.True(applied.HoldsMailStores);
    }

    /// <summary>
    /// Windows empties a Recycle Bin whole, so a bin holding a store cannot be handed to it. The bin is
    /// protected on its contents beside the store, for the reason an indivisible removal's subject is.
    /// </summary>
    [Fact]
    public void ARecycleBinWindowsWouldEmptyWholeIsWithheldWhenItHoldsAStore()
    {
        const string bin = @"D:\$Recycle.Bin\S-1-5-21-1111111111-2222222222-3333333333-1001";
        var store = bin + @"\$RA1B2C3.pst";

        var empty = new EmptyRecycleBinStep(bin, "A bin") { MailStores = [store] };

        var applied = MailStorePlan.Apply(Planning(empty));

        Assert.Empty(applied.Steps);
        Assert.Equal(2, applied.ProtectedPaths.Count);
        Assert.Contains(applied.ProtectedPaths, p => p.Path == store && p.Withheld == Withholding.MailStore);
        Assert.Contains(applied.ProtectedPaths, p => p is { Path: bin, HeldContentBefore: true, Withheld: Withholding.None });
    }

    /// <summary>One store named by two steps, in two spellings, is one file and one protection.</summary>
    [Fact]
    public void AStoreNamedTwiceIsProtectedOnce()
    {
        var clear = new ClearDirectoryStep(@"C:\Users\testuser\AppData\Local\Temp", "Temporary files")
        {
            MailStores = [Archive],
        };

        var nested = new DeleteDirectoryStep(@"C:\Users\testuser\AppData\Local\Temp\Temp1_mail.zip", "An archive")
        {
            MailStores = [Archive.ToUpperInvariant()],
        };

        var applied = MailStorePlan.Apply(Planning(clear, nested));

        Assert.Single(applied.ProtectedPaths);
    }
}
