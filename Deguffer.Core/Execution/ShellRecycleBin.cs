using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Deguffer.Core.Execution;

/// <summary>
/// One shell operation that moves each of <paramref name="paths"/> to the Recycle Bin, telling the
/// sink at the same position what became of that item. Answers why the operation as a whole failed,
/// or null where it ran.
/// </summary>
internal delegate string? RecycleOperation(IReadOnlyList<string> paths, IReadOnlyList<BinnedItem> sinks);

/// <summary>
/// The real Recycle Bin, through the shell's <c>IFileOperation</c>.
///
/// <para><c>IFileOperation</c> rather than a flag on <see cref="DirectoryRemover"/>, and the
/// distinction is a safety one rather than a convenience. That remover deletes outright and must go
/// on doing so: it exists for the ten-gigabyte package cache, and a Recycle Bin that receives ten
/// gigabytes has reclaimed nothing at all. §8's fourth question settles that for Storage. §7.1
/// settles the other half for Explore — the one file a user picked out of a picture is exactly the
/// case where recovery is available, and where it is available it is not optional.</para>
///
/// <para><b>One operation per batch, one sink per item.</b> Each item is queued with a progress sink
/// of its own (<see cref="BinnedItem"/>), which hears that item's result and where the bin put it, so
/// an outright deletion is reported for the item it happened to and no other. Measured on 2026-11-05:
/// the shell tells each item's own sink, and an item it refuses leaves the rest of its batch to
/// go.</para>
///
/// <para>Stateless, so one instance serves the process (G5). Each call makes and releases its own
/// COM objects, because a shell operation object is single-use once
/// <c>PerformOperations</c> has run.</para>
/// </summary>
public sealed class ShellRecycleBin : IRecycleBin
{
    public static ShellRecycleBin Default { get; } = new(Perform, RecycleBinReach.Default);

    // FOF_SILENT | FOF_NOCONFIRMATION | FOF_ALLOWUNDO | FOF_NOERRORUI, with FOFX_RECYCLEONDELETE.
    //
    // ALLOWUNDO *asks* for the Recycle Bin; the shell falls back to deleting outright whenever the
    // item cannot go there. Ordinarily it warns first, and the three suppression flags below are
    // exactly what silences that warning. FOFX_RECYCLEONDELETE was set to make the operation fail
    // instead, and measured on 2026-10-09 it does not: an item longer than its volume's bin limit,
    // anything sent to a bin set to keep nothing, and a path of MAX_PATH or more were each deleted
    // outright, the operation reporting success. So RecycleBinReach is asked first, and the
    // progress sink's word on what reached the bin is read afterwards. Without either, this route
    // would report "moved to the Recycle Bin" about a file that no longer exists anywhere, and §5.6
    // would not catch it because the siblings did survive. Explore ranks by size and points the
    // user at the largest thing on the drive, which is precisely what exceeds a bin's limit.
    //
    // The suppression flags cover the shell's own windows: this app has already asked the user, and
    // a second modal dialog it does not own — parentless, because handing an HWND down here would
    // put a UI concept in Core — is a dialog appearing behind the window that caused it.
    //
    // Not FOFX_EARLYFAILURE: with it, the first item the shell refuses stops every item after it in
    // the batch, measured on 2026-11-05. Without it, each item's sink hears that item's refusal and
    // the others still go, as they did when each item was an operation of its own.
    private const uint OperationFlags = 0x0004 | 0x0010 | 0x0040 | 0x0400 | 0x00080000;

    private static readonly Guid FileOperationClass = new("3ad05575-8857-4850-9277-11b85bdb8e09");

    private readonly RecycleOperation _perform;
    private readonly RecycleBinReach _reach;

    /// <param name="perform">
    /// The shell's move of a batch. A seam, so a test can show that an item the bin cannot take never
    /// reaches the shell, and what each item's sink is told becomes that item's outcome, without
    /// asking the shell to delete anything.
    /// </param>
    /// <param name="reach">What the bin can take, asked before the shell is.</param>
    internal ShellRecycleBin(RecycleOperation perform, RecycleBinReach reach)
    {
        _perform = perform;
        _reach = reach;
    }

    public string? WhyItCannotTake(string path, CancellationToken ct = default) => _reach.WhyNot(path, ct);

    public IReadOnlyList<RecycleOutcome> Recycle(
        IReadOnlyList<string> paths, CancellationToken ct = default, Func<int, RecycleOutcome, bool>? stopsAfter = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(paths.Count, IRecycleBin.LargestBatch, nameof(paths));

        foreach (var path in paths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path, nameof(paths));
        }

        var outcomes = new RecycleOutcome[paths.Count];

        if (Asked(paths, outcomes, ct) is not { Count: > 0 } handed)
        {
            return outcomes;
        }

        // Each item's outcome is put to the caller's rule as soon as the shell has dealt with it, so a
        // stop takes effect at the next item rather than once the batch has gone.
        StrongBox<bool> stop = new();
        var sinks = handed
            .Select(index => new BinnedItem(ct, stop, stopsAfter is null ? null : sink => stopsAfter(index, Outcome(sink, null, stop, ct))))
            .ToArray();
        string? failed = "The Recycle Bin operation did not run.";

        // The shell's apartment requirement, met on a thread of our own rather than by initialising
        // whatever thread the caller arrived on. Core is called from a thread-pool thread by every
        // caller it has, and CoInitialize on one of those outlives the call — it would change the
        // apartment of a thread the runtime hands to something else next.
        var thread = new Thread(() => failed = _perform([.. handed.Select(index => paths[index])], sinks));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        for (var i = 0; i < handed.Count; i++)
        {
            outcomes[handed[i]] = Outcome(sinks[i], failed, stop, ct);
        }

        return outcomes;
    }

    /// <summary>
    /// Asks of each item whether the bin can take it, immediately before the batch goes, refusing in
    /// <paramref name="outcomes"/> each it cannot, and answers the positions of those to hand the
    /// shell. A cancel while asking hands it nothing, so every item not refused is not reached.
    /// </summary>
    private List<int> Asked(IReadOnlyList<string> paths, RecycleOutcome[] outcomes, CancellationToken ct)
    {
        List<int> handed = [];

        try
        {
            for (var i = 0; i < paths.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                if (WhyItCannotTake(paths[i], ct) is { } cannotTake)
                {
                    outcomes[i] = new RecycleOutcome(Removed: false, RecycleOutcome.CannotTake(cannotTake));
                }
                else
                {
                    handed.Add(i);
                }
            }
        }
        catch (OperationCanceledException)
        {
            for (var i = 0; i < paths.Count; i++)
            {
                outcomes[i] ??= RecycleOutcome.NotReached;
            }

            return [];
        }

        return handed;
    }

    private static string? Perform(IReadOnlyList<string> paths, IReadOnlyList<BinnedItem> sinks)
    {
        object? operation = null;

        try
        {
            operation = Activator.CreateInstance(Type.GetTypeFromCLSID(FileOperationClass)!);
            var file = (IFileOperation)operation!;
            file.SetOperationFlags(OperationFlags);

            var queued = 0;

            for (var i = 0; i < paths.Count; i++)
            {
                if (Queue(file, paths[i], sinks[i]))
                {
                    queued++;
                }
            }

            if (queued > 0)
            {
                file.PerformOperations();
            }

            return null;
        }
        catch (COMException ex)
        {
            // A refusal of the whole operation, or the stop a sink asked for when the removal was
            // cancelled. Each item the shell reached was told its own result first, and that result
            // is what its outcome reads; this is the answer only for an item it never reached.
            return Refused(ex.HResult);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidCastException)
        {
            // A machine where the file-operation class is not registered.
            return $"Windows would not move this to the Recycle Bin: {ex.Message} It is still where it was.";
        }
        finally
        {
            if (operation is not null)
            {
                Marshal.FinalReleaseComObject(operation);
            }
        }
    }

    /// <summary>
    /// Queues the move of the item at <paramref name="path"/> with its own sink, or tells the sink why
    /// the shell would not take it, leaving the rest of the batch to go.
    /// </summary>
    private static bool Queue(IFileOperation file, string path, BinnedItem sink)
    {
        try
        {
            var itemId = typeof(IShellItem).GUID;
            ShellNative.SHCreateItemFromParsingName(path, IntPtr.Zero, ref itemId, out var item);
            file.DeleteItem(item, sink);
            return true;
        }
        catch (COMException ex)
        {
            // Every refusal of an item the shell has arrives this way: a path it will not parse, a
            // volume with no Recycle Bin, an item too large for the bin's quota, and a file something
            // else holds open. They are told apart by HRESULT and none of them is this code's to
            // fix, so the number goes in the message rather than being mapped to a guess.
            sink.Refuse(Refused(ex.HResult));
            return false;
        }
        catch (ArgumentException ex)
        {
            // A path the shell's own parser rejected before it got as far as an HRESULT.
            sink.Refuse($"Windows would not move this to the Recycle Bin: {ex.Message} It is still where it was.");
            return false;
        }
    }

    private static string Refused(int result) =>
        $"Windows would not move this to the Recycle Bin (0x{result:X8}). It is still where it was.";

    /// <summary>
    /// What a finished operation did to one item, as its sink heard it. An outright deletion is
    /// answered first, because whatever else the shell says, the item is gone and not in the bin. An
    /// item the shell never reached is not reached where the removal was cancelled, stopped before
    /// where the caller's rule stopped the batch, and otherwise takes the operation's own failure.
    /// </summary>
    /// <param name="failed">Why the operation as a whole failed, or null where it ran.</param>
    /// <param name="stop">Whether the caller's rule stopped the batch at an earlier item.</param>
    internal static RecycleOutcome Outcome(BinnedItem sink, string? failed, StrongBox<bool> stop, CancellationToken ct) => sink switch
    {
        { DeletedOutright: true } => new RecycleOutcome(
            Removed: true,
            "Windows deleted this outright rather than moving it to the Recycle Bin, so it cannot be restored from there.")
        {
            DeletedOutright = true,
        },
        { Moved: true } => new RecycleOutcome(Removed: true) { Binned = sink.Path },
        { StoppedHere: true } => RecycleOutcome.NotReached,
        { HeldBack: true } => RecycleOutcome.StoppedBefore,
        { Failure: { } result } => new RecycleOutcome(Removed: false, Refused(result)),
        { Refusal: { } why } => new RecycleOutcome(Removed: false, why),
        _ when ct.IsCancellationRequested => RecycleOutcome.NotReached,
        _ when stop.Value => RecycleOutcome.StoppedBefore,
        _ => new RecycleOutcome(Removed: false, failed ?? "Windows stopped before moving this to the Recycle Bin."),
    };
}
