namespace Deguffer.Core.InstalledApps;

/// <summary>What a selection of stale rows may remove, and what the page says about the rest.</summary>
/// <param name="Removable">The entries a removal would act on.</param>
/// <param name="Note">Why the rest will be left, or null where nothing will be.</param>
public sealed record RemovalSelection(IReadOnlyList<InstalledEntry> Removable, string? Note)
{
    public static RemovalSelection For(IReadOnlyList<InstalledEntry> selected, bool isElevated)
    {
        var verdicts = selected.Select(e => (Entry: e, Verdict: EntryRemovalPolicy.MayRemove(e, isElevated))).ToList();
        var removable = verdicts.Where(v => v.Verdict.IsAllowed).Select(v => v.Entry).ToList();
        var refused = verdicts.Where(v => !v.Verdict.IsAllowed).ToList();

        return new RemovalSelection(removable, LeftNote.For(refused, removable.Count, "entries"));
    }
}
