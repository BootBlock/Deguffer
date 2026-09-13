namespace Deguffer.Core.Memory.Acting;

/// <summary>
/// The processes §7.2.1's second §5.6 assertion is about: the shell window's owner, and the
/// compositor of Deguffer's own session.
///
/// <para>Asking an ordinary program to close cannot end the desktop, and neither of these exits by
/// itself while a user is signed in, so their survival is exact and a run that loses one fails.
/// Every other process the refusal table protects is deliberately not here: Deguffer's own tree and
/// an <c>explorer.exe</c> that is not the shell exit on their own in ordinary use, and a watch with
/// no deadline would turn that into a false alarm.</para>
/// </summary>
internal static class DesktopProcesses
{
    /// <summary>The image Windows gives the compositor, one process of which runs per session.</summary>
    private const string CompositorName = "dwm.exe";

    /// <param name="before">The read taken as the action began, which is where both are named from.</param>
    /// <param name="shellOwner">The process Windows reports as owning the shell window, or null where it would not say.</param>
    /// <param name="facts">
    /// Where each compositor's session is asked. One or two processes are opened here, at the moment
    /// of the action and never for a row nobody selected (§7.2).
    /// </param>
    public static IReadOnlyList<ProcessMemory> Of(
        MemorySnapshot before,
        int? shellOwner,
        IProcessFactSource facts,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(facts);

        var measured = ProcessTree.Measured(before);
        var desktop = new List<ProcessMemory>();

        if (shellOwner is { } shell && measured.FirstOrDefault(p => p.ProcessId == shell) is { } owner)
        {
            desktop.Add(owner);
        }

        foreach (var compositor in measured)
        {
            if (!compositor.Name.Equals(CompositorName, StringComparison.OrdinalIgnoreCase)
                || desktop.Any(p => ProcessTree.Identity(p) == ProcessTree.Identity(compositor)))
            {
                continue;
            }

            // Kept unless Windows says outright that it belongs to another session. A session that
            // will not answer leaves the question open, and a fact nobody established is not grounds
            // for dropping a process out of the one assertion this action can make.
            if (facts.Read(compositor.ProcessId, compositor.CreationTime!.Value, ct).InOwnSession != Answer.No)
            {
                desktop.Add(compositor);
            }
        }

        return desktop;
    }
}
