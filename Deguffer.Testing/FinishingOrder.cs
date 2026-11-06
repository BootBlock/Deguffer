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
    /// <summary>Far longer than a finding takes to arrive, and far shorter than a hung test.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly IReadOnlyList<FakeCleanupProvider> _order;
    private readonly TaskCompletionSource[] _arrived;

    /// <param name="order">The providers in the order they are to finish, which sets each one's <see cref="FakeCleanupProvider.WhilePlanning"/>.</param>
    public FinishingOrder(params IReadOnlyList<FakeCleanupProvider> order)
    {
        _order = order;
        _arrived = [.. order.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))];

        for (var index = 1; index < order.Count; index++)
        {
            var previous = order[index - 1];
            var previousArrived = _arrived[index - 1].Task;

            order[index].WhilePlanning = () =>
            {
                if (!previousArrived.Wait(Patience))
                {
                    throw new TimeoutException($"The finding of '{previous.Id}' never arrived, so the provider after it could not finish.");
                }
            };
        }
    }

    /// <summary>Record that a finding arrived, which lets the provider after its own finish.</summary>
    public void Report(Finding value)
    {
        for (var index = 0; index < _order.Count; index++)
        {
            if (ReferenceEquals(_order[index], value.Provider))
            {
                _arrived[index].TrySetResult();
            }
        }
    }
}
