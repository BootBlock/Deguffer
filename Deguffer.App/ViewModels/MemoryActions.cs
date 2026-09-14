using Deguffer.App.Shell;
using Deguffer.Core.Memory;
using Deguffer.Core.Memory.Acting;

namespace Deguffer.App.ViewModels;

/// <summary>
/// Asks about a Memory close and carries it out.
///
/// <para>Separate from <see cref="MemorySelection"/> because the two have different subjects. That
/// one is about what the user picked and what the screen says about it; this one is about what
/// happens to a program. Keeping them apart is G1 applied to a page that would otherwise be reading
/// the machine, drawing it, formatting it <em>and</em> closing things on it.</para>
///
/// <para>It decides nothing. What may be closed is <see cref="MemoryActionPolicy"/>'s, what the user
/// is told is <see cref="MemoryClosePrompt"/>'s, and what happened is <see cref="CloseReport"/>'s —
/// all in Core, all provable without a WinUI host.</para>
/// </summary>
public sealed class MemoryActions
{
    private readonly MemoryActionPolicy _policy;
    private readonly ProcessCloser _closer;
    private readonly Func<IMemoryConfirmationPrompt> _prompt;

    public MemoryActions(
        MemoryActionPolicy policy,
        ProcessCloser closer,
        Func<IMemoryConfirmationPrompt> prompt)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(closer);
        ArgumentNullException.ThrowIfNull(prompt);

        _policy = policy;
        _closer = closer;
        _prompt = prompt;
    }

    /// <summary>
    /// The policy and the closer this machine's own calls answer, and the dialog the user meets.
    ///
    /// <para>Nothing is built in the background, as Explore's policy is: §7.2.1 reads the machine for
    /// the one process the user picked, at the moment they pick it, so there is no probing to do in
    /// advance and nothing to keep up to date between selections.</para>
    /// </summary>
    public static MemoryActions ForThisMachine(Func<IMemoryConfirmationPrompt> prompt) =>
        new(
            new MemoryActionPolicy(ProcessFactSource.Default, DesktopFacts.Default),
            ProcessCloser.For(MemorySource.Default),
            prompt);

    /// <summary>
    /// Whether Memory will ask <paramref name="target"/> to close, and what to say either way
    /// (§7.2.1). Read for the one process the user picked, and for no other.
    ///
    /// <para>Off the caller's thread, because deciding opens the process and asks Windows six
    /// questions about it, and the caller is a window.</para>
    /// </summary>
    public Task<MemoryVerdict> VerdictAsync(
        MemorySnapshot snapshot, ProcessMemory target, CancellationToken ct) =>
        Task.Run(() => _policy.For(snapshot, target, ct), ct);

    /// <summary>
    /// Ask the user, then ask the program. Null when the user declined, which is a decision rather
    /// than a failure.
    /// </summary>
    /// <param name="windows">How many of the target's windows the verdict found, for the dialog to name.</param>
    /// <param name="listing">
    /// How much of the service list the reading obtained, for the dialog to say what the refusal of a
    /// service host could not reach (§7.2.1).
    /// </param>
    /// <param name="watching">Given the report the moment the last message is posted.</param>
    /// <param name="ct">Ends the watch. The messages are already posted, and a close cannot be called off.</param>
    public async Task<CloseAttempt?> CloseAsync(
        ProcessMemory target,
        int windows,
        ServiceListing listing,
        IProgress<CloseReport> watching,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);

        var prompt = MemoryClosePrompt.For(target, windows, listing);

        // Asked before anything is opened, and never after: a dialog the user says yes to is the
        // whole of the authority this action has.
        if (!await _prompt().AskAsync(prompt, ct).ConfigureAwait(true))
        {
            return null;
        }

        return await _closer.CloseAsync(target, watching, ct).ConfigureAwait(true);
    }
}
