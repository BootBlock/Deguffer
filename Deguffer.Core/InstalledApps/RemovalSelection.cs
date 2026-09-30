namespace Deguffer.Core.InstalledApps;

/// <summary>What a selection of stale rows may remove, and what the page says about the rest.</summary>
/// <param name="Removable">The entries a removal would act on.</param>
/// <param name="Note">Why the rest will be left, or null where nothing will be.</param>
/// <param name="NeedsElevation">Whether administrator rights would let the rest be removed too.</param>
public sealed record RemovalSelection(IReadOnlyList<InstalledEntry> Removable, string? Note, bool NeedsElevation)
{
    public static RemovalSelection For(IReadOnlyList<InstalledEntry> selected, bool isElevated)
    {
        var verdicts = selected.Select(e => (Entry: e, Verdict: EntryRemovalPolicy.MayRemove(e, isElevated))).ToList();
        var removable = verdicts.Where(v => v.Verdict.IsAllowed).Select(v => v.Entry).ToList();
        var refused = verdicts.Where(v => !v.Verdict.IsAllowed).ToList();

        if (refused.Count == 0)
        {
            return new RemovalSelection(removable, null, NeedsElevation: false);
        }

        var note = refused is [var only]
            ? $"'{only.Entry.Name}' will be left. {only.Verdict.Reason}"
            : $"{refused.Count} of the selected entries will be left. '{refused[0].Entry.Name}': {refused[0].Verdict.Reason}";

        return new RemovalSelection(removable, note, refused.Any(r => r.Verdict.NeedsElevation));
    }
}
