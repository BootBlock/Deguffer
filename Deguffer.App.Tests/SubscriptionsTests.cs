using Deguffer.App.Shell;

namespace Deguffer.App.Tests;

/// <summary>
/// How a control's subscriptions, made in <c>Loaded</c>, are undone in <c>Unloaded</c>: every one, by
/// one dispose, so the two cannot drift apart.
/// </summary>
public sealed class SubscriptionsTests
{
    private event EventHandler? Raised;

    [Fact]
    public void A_subscription_is_made_when_it_is_added_and_undone_when_disposed()
    {
        var heard = 0;
        void OnRaised(object? sender, EventArgs e) => heard++;

        var subscriptions = new Subscriptions().Add(() => Raised += OnRaised, () => Raised -= OnRaised);
        Raised?.Invoke(this, EventArgs.Empty);

        subscriptions.Dispose();
        Raised?.Invoke(this, EventArgs.Empty);

        Assert.Equal(1, heard);
        Assert.Null(Raised);
    }

    [Fact]
    public void Every_subscription_is_undone_the_last_made_first()
    {
        var undone = new List<string>();

        var subscriptions = new Subscriptions()
            .Add(() => { }, () => undone.Add("first"))
            .Add(() => { }, () => undone.Add("second"));

        subscriptions.Dispose();

        Assert.Equal(["second", "first"], undone);
    }

    [Fact]
    public void A_second_dispose_undoes_nothing_again()
    {
        var undone = 0;
        var subscriptions = new Subscriptions().Add(() => { }, () => undone++);

        subscriptions.Dispose();
        subscriptions.Dispose();

        Assert.Equal(1, undone);
    }
}
