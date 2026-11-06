using Deguffer.Core.Execution;

namespace Deguffer.Testing;

/// <summary>
/// Providers made to finish planning in a stated order: each one's plan waits until the finding of the
/// one before it has arrived. A sleep only makes that order likely, and a busy machine that holds a
/// planning thread back for longer than the sleep lands the findings in another order.
///
/// <para>What counts as arriving is the caller's to say, by reporting the finding here: the planner's
/// own report of it, or a row landing on a page. A provider whose turn never comes fails its plan
/// rather than hanging the test.</para>
/// </summary>
public sealed class FinishingOrder : IProgress<Finding>
{
    private readonly CompletionOrder<string> _order;

    /// <param name="order">The providers in the order they are to finish, which sets each one's <see cref="FakeCleanupProvider.WhilePlanning"/>.</param>
    public FinishingOrder(params IReadOnlyList<FakeCleanupProvider> order)
    {
        _order = new CompletionOrder<string>(order.Select(provider => provider.Id));

        foreach (var provider in order)
        {
            // A provider plans on a thread of its own and has no way to wait asynchronously.
            provider.WhilePlanning = () => _order.TurnOf(provider.Id).GetAwaiter().GetResult();
        }
    }

    /// <summary>Record that a finding arrived, which lets the provider after its own finish.</summary>
    public void Report(Finding value)
    {
        // Findings of providers outside the order arrive too, and hold nothing up.
        if (_order.Contains(value.Provider.Id))
        {
            _order.Complete(value.Provider.Id);
        }
    }
}
