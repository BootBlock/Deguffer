using Deguffer.Core.Execution;

namespace Deguffer.Core.Providers;

/// <summary>
/// Whether Vagrant's prune, run now, would still remove only the versions the plan showed and no
/// version a machine was created from.
///
/// <para><b>Prune decides again when it runs.</b> It ranks every installed version from
/// <c>BoxCollection#all</c> each time (<c>plugins/commands/box/command/prune.rb</c>), so a newer
/// version installed after the preview, by <c>vagrant box update</c> or a <c>vagrant up</c> that
/// downloads one, makes the version the preview kept one it removes. And a machine created after the
/// preview from a version it removes is protected only by <c>Box#in_use?</c>, which misses a box
/// installed with an architecture (see <see cref="VagrantMachineIndexReading.CreatedFrom"/>). Neither
/// reaches §5.6, because a folder the plan never listed is not one it protects.</para>
///
/// <para>So the step runs only while the box folders read as they did before the dry run, which makes
/// prune's removal set the dry run's, and while the machine index records no machine on a version in
/// it. The layout being the one the plan saw also keeps the plan's own finding that no removed version
/// is installed for two architectures, so that is not read a second time.</para>
/// </summary>
/// <param name="home">Vagrant's home, as prune will find it: the variable is the process's own.</param>
/// <param name="installed">The box folders read before the dry run, from <see cref="VagrantBoxLayout.Installed"/>.</param>
/// <param name="removed">Each version the dry run said prune would remove, once.</param>
internal sealed class VagrantPruneTarget(
    string home,
    IReadOnlySet<string> installed,
    IReadOnlyList<VagrantBoxVersion> removed) : ICommandTargetCheck
{
    public string? WhyNot(RunCommandStep step, CancellationToken ct)
    {
        var boxes = VagrantBoxLayout.BoxesIn(home);

        if (VagrantBoxLayout.Installed(boxes) is not { } now)
        {
            return $"Deguffer could not read every box folder in {boxes} again, so it cannot tell which "
                + "versions Vagrant's prune would remove now";
        }

        if (!now.SetEquals(installed))
        {
            return $"the boxes installed in {boxes} have changed since the preview, and Vagrant's prune "
                + "chooses again when it runs which versions are superseded, so it could remove one the "
                + "preview kept. Scan again to see what it would remove now";
        }

        var index = VagrantMachineIndex.Read(home);

        if (index.Unread is { } unread)
        {
            return $"{unread.Opening("Vagrant's machine index")}, so Deguffer cannot tell whether a machine "
                + "has been created since the preview from a version prune would remove";
        }

        foreach (var box in removed)
        {
            if (index.CreatedFrom(box) is { Count: > 0 } machines)
            {
                return $"Vagrant now records {string.Join(", ", machines.Select(VagrantPrunePlanNotes.Describe))} "
                    + $"as created from {VagrantPrunePlanNotes.Describe(box)}, which the prune would remove";
            }
        }

        return null;
    }
}
