using Microsoft.UI.Composition.Interactions;

namespace Deguffer.App.Controls;

/// <summary>
/// An interaction tracker's owner that hands each of the tracker's reports on to the real owner for as
/// long as anything else keeps that owner alive.
///
/// <para>A tracker is a native object that holds its owner with a plain COM reference, which the
/// collector treats as a root and which WinUI's reference tracking does not see. A map's
/// <see cref="ExploreZoom"/> given to the tracker as its owner kept itself, and through its events the
/// whole map, alive for the life of the process (#737). The tracker holds this, which reaches
/// the zoom only weakly, so the zoom goes with the map that holds it. A report that arrives after it
/// has gone is dropped: nothing is left to move.</para>
/// </summary>
internal sealed class WeakTrackerOwner(IInteractionTrackerOwner owner) : IInteractionTrackerOwner
{
    private readonly WeakReference<IInteractionTrackerOwner> _owner = new(owner);

    public void CustomAnimationStateEntered(InteractionTracker sender, InteractionTrackerCustomAnimationStateEnteredArgs args)
    {
        if (_owner.TryGetTarget(out var owner))
        {
            owner.CustomAnimationStateEntered(sender, args);
        }
    }

    public void IdleStateEntered(InteractionTracker sender, InteractionTrackerIdleStateEnteredArgs args)
    {
        if (_owner.TryGetTarget(out var owner))
        {
            owner.IdleStateEntered(sender, args);
        }
    }

    public void InertiaStateEntered(InteractionTracker sender, InteractionTrackerInertiaStateEnteredArgs args)
    {
        if (_owner.TryGetTarget(out var owner))
        {
            owner.InertiaStateEntered(sender, args);
        }
    }

    public void InteractingStateEntered(InteractionTracker sender, InteractionTrackerInteractingStateEnteredArgs args)
    {
        if (_owner.TryGetTarget(out var owner))
        {
            owner.InteractingStateEntered(sender, args);
        }
    }

    public void RequestIgnored(InteractionTracker sender, InteractionTrackerRequestIgnoredArgs args)
    {
        if (_owner.TryGetTarget(out var owner))
        {
            owner.RequestIgnored(sender, args);
        }
    }

    public void ValuesChanged(InteractionTracker sender, InteractionTrackerValuesChangedArgs args)
    {
        if (_owner.TryGetTarget(out var owner))
        {
            owner.ValuesChanged(sender, args);
        }
    }
}
