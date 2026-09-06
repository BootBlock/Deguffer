using System.Runtime.InteropServices;

namespace Deguffer.Core.Execution;

/// <param name="Emptied">Whether the shell reported that it emptied the bin.</param>
/// <param name="Message">
/// What happened, written for the user, or null where nothing needs saying. Never null on a
/// failure: the shell refuses for reasons this code cannot fix, and the number it refused with is
/// the only thing that tells one from another.
/// </param>
public sealed record RecycleBinEmptyOutcome(bool Emptied, string? Message = null);

/// <summary>
/// Emptying one volume's Recycle Bin through Windows, behind an interface for the reason
/// <see cref="IRecycleBin"/> is: §6.3 is a requirement about the <em>form</em> of the path that
/// crosses into Win32, and no outcome of a deletion can demonstrate it.
///
/// <para>The form required is the shell's, not the file APIs': a volume root in display form, with
/// no extended-length prefix. <c>SHEmptyRecycleBin</c> parses <c>D:\</c> and refuses
/// <c>\?\D:\</c>, the same way <c>SHCreateItemFromParsingName</c> does.</para>
///
/// <para>It also exists so the executor can be driven with no volume to empty. A test that called
/// the real shell would empty the Recycle Bin of whoever ran the suite, which is the one thing a
/// test in this project must never do.</para>
/// </summary>
public interface IRecycleBinEmptier
{
    /// <param name="volumeRoot">
    /// The root of the volume whose bin is to be emptied, in display form. The caller supplies it;
    /// this does not derive it, because a seam that worked the path out for itself would make the
    /// assertion about what crosses it unfalsifiable.
    /// </param>
    RecycleBinEmptyOutcome Empty(string volumeRoot);
}

/// <summary>
/// The real one, through <c>SHEmptyRecycleBin</c>.
///
/// <para><b>It names a volume and empties one account's bin, and that gap is where §5.2 lives.</b>
/// The call takes a volume root with no account beside it, so from the signature alone it looks
/// like exactly the over-broad rule §5.2 exists to refuse — the bin root is a shared parent, and
/// another person's deleted files sit beside this user's under it. What was measured is that the
/// shell scopes the operation to the account the process runs as. On a scratch volume carrying this
/// user's own bin, a sibling directory named for a second account's identifier, and a child that
/// was not an identifier at all, an <em>elevated</em> call on the volume root removed this account's
/// entries and left all three of the others exactly as they were, the bin root included. Elevation
/// is the case worth stating because it is the one where a broader rule could have reached further,
/// and it did not: a token that may delete anything did not widen what the shell chose to delete.
/// §5.6 still asserts the siblings afterwards, so the property is proved again on every run rather
/// than resting on that measurement.</para>
///
/// <para><b>It is far slower than deleting the same files, and that is the accepted cost rather than
/// a surprise.</b> Measured on a scratch volume, against a bin holding 1,000 recycled files as 2,000
/// <c>$I</c> and <c>$R</c> entries: this route took 4.2, 4.5 and 5.8 seconds across three passes,
/// where removing the same tree by path took 0.15, 0.17 and 0.17. At 3,000 files the two were 60.0
/// seconds and 0.68. The gap widens with the number of entries rather than holding, so the cost is
/// worst on exactly the bin worth emptying. What it buys is the notification: Windows is told the
/// bin changed, so an open Recycle Bin window, its icon and anything else listening agree with the
/// disk immediately. <see cref="Configuration.AppPreferences.EmptyRecycleBinsDirectly"/> is the
/// setting that takes the other side of that trade.</para>
///
/// <para>Stateless, so one instance serves the process (G5).</para>
/// </summary>
public sealed class ShellRecycleBinEmptier : IRecycleBinEmptier
{
    public static ShellRecycleBinEmptier Default { get; } = new();

    // SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND.
    //
    // All three suppress a window this app does not own. Deguffer has already previewed what it
    // will remove and asked for the confirmation §7 requires of Tier 3, so the shell's own
    // "are you sure" is a second question about a decision already taken — and it would arrive
    // parentless, because handing an HWND down here would put a UI concept in Core, which means
    // appearing behind the window that caused it.
    private const uint EmptyFlags = 0x00000001 | 0x00000002 | 0x00000004;

    private ShellRecycleBinEmptier()
    {
    }

    public RecycleBinEmptyOutcome Empty(string volumeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeRoot);

        // Fails closed on anything that is not a volume root, because the scope of
        // SHEmptyRecycleBin over one is what was measured and the scope over anything else is not
        // stated anywhere. A caller that has derived the wrong path — a bin that is not laid out
        // where the derivation assumes, a fixture standing a synthetic volume inside a folder —
        // gets a refusal it can read, rather than a call whose reach nobody established.
        // Fully qualified as well as a root, because "C:" is its own root and is drive-*relative* —
        // it means the current directory on C:, which is a different path on every process and not
        // one anybody named. Only the trailing separator distinguishes it from the root itself.
        if (!Path.IsPathFullyQualified(volumeRoot)
            || !string.Equals(Path.GetPathRoot(volumeRoot), volumeRoot, StringComparison.OrdinalIgnoreCase))
        {
            return new RecycleBinEmptyOutcome(
                Emptied: false,
                $"'{volumeRoot}' is not the root of a drive, so Windows was not asked to empty it.");
        }

        // The shell's apartment requirement, met on a thread of our own rather than by initialising
        // whatever thread the caller arrived on, exactly as ShellRecycleBin does and for the same
        // reason: Core is called from a thread-pool thread, and CoInitialize on one of those
        // outlives the call.
        //
        // Nothing here is cancellable. SHEmptyRecycleBin offers no way to stop it once it has
        // started, and abandoning the wait would leave the shell still deleting while the executor
        // went on to measure what was left. The caller checks for cancellation before starting,
        // which is the whole of what can honestly be offered.
        var outcome = new RecycleBinEmptyOutcome(
            Emptied: false, "The Recycle Bin was not emptied.");

        var thread = new Thread(() => outcome = Perform(volumeRoot));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return outcome;
    }

    private static RecycleBinEmptyOutcome Perform(string volumeRoot)
    {
        int hresult;

        try
        {
            hresult = ShellNative.SHEmptyRecycleBin(IntPtr.Zero, volumeRoot, EmptyFlags);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // A Windows install without the shell entry point at all. Nothing to fall back to from
            // here: the plan named this route, so saying so beats deleting by a route the user did
            // not choose.
            return new RecycleBinEmptyOutcome(
                Emptied: false,
                $"Windows would not empty this Recycle Bin: {ex.Message}");
        }

        // S_OK is the only success. The shell reports a bin it could not read, a volume whose bin is
        // switched off, and a file something else holds open as distinct HRESULTs, none of which is
        // this code's to fix — so the number goes to the user rather than being mapped to a guess.
        return hresult == 0
            ? new RecycleBinEmptyOutcome(Emptied: true)
            : new RecycleBinEmptyOutcome(
                Emptied: false,
                $"Windows would not empty this Recycle Bin (0x{hresult:X8}).");
    }
}
