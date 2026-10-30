using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What a Vagrant prune plan tells the user: why the prune is withheld, what it would remove and
/// keep, and what running Vagrant does besides.
/// </summary>
internal static class VagrantPrunePlanNotes
{
    /// <summary>How many versions a note names before it summarises the rest.</summary>
    private const int VersionsNamed = 8;

    /// <summary>
    /// Every reason not to run the prune, or none. All of them, so a user who acts on the notes does
    /// not meet the next reason only on the next scan.
    /// </summary>
    /// <param name="located">Each version prune would remove, once, with the provider folders it is in.</param>
    public static IReadOnlyList<PlanNote> Blockers(
        VagrantPrunePreview preview,
        IReadOnlyList<(VagrantBoxVersion Box, IReadOnlyList<string> Folders)> located,
        VagrantMachineIndexReading index)
    {
        var notes = new List<PlanNote>();

        // BoxRemove raises BoxRemoveMultiArchitecture for a version installed for two architectures of
        // one provider, because prune names no architecture, and that ends the whole prune there. The
        // dry run lists such a version once for each architecture; the layout says the same thing where
        // the listing and the folders disagree.
        var split = preview.Removed
            .GroupBy(box => box)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Union(located.Where(found => found.Folders.Count > 1).Select(found => found.Box));

        notes.AddRange(split.Select(box => Information(
            $"Nothing is offered, because {Describe(box)} is installed for more than one architecture, and "
            + "Vagrant's prune stops with an error part-way through when it reaches a version like that. "
            + $"'vagrant box remove {box.Name} --box-version {box.Version} --provider {box.Provider} "
            + "--architecture <architecture>' removes one architecture at a time.")));

        if (index.Unread is { } unread)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Warning,
                $"{unread.Opening("Vagrant's machine index")}, so Deguffer cannot tell whether a machine was "
                + "created from a version prune would remove, and offers nothing."));
        }

        foreach (var box in preview.Removed.Distinct())
        {
            var machines = index.CreatedFrom(box);

            if (machines.Count > 0)
            {
                notes.Add(Information(
                    $"Nothing is offered, because Vagrant's prune would remove {Describe(box)}, and Vagrant "
                    + $"records {string.Join(", ", machines.Select(Describe))} as created from it. Vagrant's "
                    + "own check for a box in use does not reliably recognise that, so it could remove a "
                    + "version a machine still needs: a Hyper-V linked clone's disk depends on the box's "
                    + "image. Destroy the machine first, or run 'vagrant global-status --prune' if it no "
                    + "longer exists."));
            }
        }

        return notes;
    }

    /// <summary>What prune named, as the evidence behind the figure.</summary>
    public static IReadOnlyList<PlanNote> Evidence(
        VagrantPrunePreview preview,
        IReadOnlyList<(VagrantBoxVersion Box, IReadOnlyList<string> Folders)> located)
    {
        List<PlanNote> notes =
        [
            Information($"Vagrant's prune would remove {Named(preview.Removed.Distinct().ToList())}."),
            Information($"It keeps the newest version of each box: {Named(preview.Kept)}."),
        ];

        var missing = located.Where(found => found.Folders.Count == 0).Select(found => found.Box).ToList();

        if (missing.Count > 0)
        {
            notes.Add(Information(
                $"Vagrant also named {Named(missing)}, which is not in a folder holding a box's "
                + "metadata.json, so it is not counted in the size shown."));
        }

        return notes;
    }

    /// <summary>
    /// What every Vagrant command does besides its own work, said because Deguffer cannot set the
    /// environment variables that would stop it.
    /// </summary>
    public static PlanNote SideEffects { get; } = Information(
        "Running Vagrant loads the Vagrantfile in Vagrant's own folder, and any Vagrantfile in the "
        + "folder Deguffer runs tools in or a folder above it, such as your profile folder. Unless "
        + "VAGRANT_CHECKPOINT_DISABLE is set, it also asks HashiCorp whether a newer Vagrant is available.");

    private static string Named(IReadOnlyList<VagrantBoxVersion> boxes)
    {
        var named = string.Join(", ", boxes.Take(VersionsNamed).Select(Describe));
        var rest = boxes.Count - VersionsNamed;

        return rest > 0 ? $"{named}, and {rest} more" : named;
    }

    private static string Describe(VagrantBoxVersion box) => $"{box.Name} {box.Version} ({box.Provider})";

    private static string Describe(VagrantMachine machine) =>
        machine.Project is { } project
            ? $"the machine '{machine.Name}' in {project}"
            : $"the machine '{machine.Name}'";

    private static PlanNote Information(string message) => new(PlanNoteSeverity.Information, message);
}
