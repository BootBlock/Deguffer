using System.Runtime.InteropServices;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <param name="Removed">Whether the item is gone from where it was.</param>
/// <param name="Message">
/// What happened, written for the user. Never null on a failure: a Recycle Bin removal has more
/// ways to be refused than an outright delete — the shell will not take a path it cannot parse, and
/// it will not recycle onto a volume with no bin — and "nothing happened" with no sentence beside it
/// is the answer a user cannot act on.
/// </param>
public sealed record RecycleOutcome(bool Removed, string? Message = null)
{
    /// <summary>
    /// Where the bin put the item, as the shell said once it had moved it, or null where the shell
    /// did not say. A duplicate removal identifies what is there, to check that the file the bin
    /// received is the file it compared (§7.4).
    /// </summary>
    public string? Binned { get; init; }

    /// <summary>
    /// Whether Windows deleted the item outright when it was asked to move it to the bin. It is gone,
    /// and it is not in the bin to restore. The shell does this, reporting success, for a path it
    /// cannot take (<see cref="RecycleBinReach"/>), so it is refused beforehand; this is what the
    /// shell's progress sink says when a path changed under that question or another reason arose.
    /// </summary>
    public bool DeletedOutright { get; init; }
}

/// <summary>
/// Moving one item to the Recycle Bin, behind an interface for the reason
/// <see cref="IFileSystem"/> is: §6.3 is a requirement about the <em>form</em> of the path that
/// crosses into Win32, and no outcome of a deletion can demonstrate it.
///
/// <para><b>The form required here is not the one <see cref="IFileSystem"/> requires</b>, and that
/// is the whole reason this is a second seam rather than a method on the first. Everything else in
/// Core hands Win32 the extended-length prefix, because the file APIs need it past
/// <c>MAX_PATH</c>. The shell namespace does not accept it at all: <c>SHCreateItemFromParsingName</c>
/// parses <c>C:\Users\…</c> and refuses <c>\\?\C:\Users\…</c>. So what crosses this boundary is the
/// fully-qualified, fully-resolved <em>display</em> path — normalised, because
/// <see cref="LongPath.Configured"/>'s trap applies to a shell call exactly as it applies to a
/// deletion, and a value carrying <c>..</c> would recycle a directory nobody named.</para>
///
/// <para>A path too long for the shell is therefore a refusal rather than a truncation, and it is
/// reported as one. Falling back to an outright delete would be worse than failing: the user asked
/// for the reversible removal, and quietly giving them the irreversible one is the single change
/// §7.1 would least tolerate. That is also why an item the bin cannot take is refused before the
/// shell is asked: the shell itself deletes such an item outright and reports success
/// (<see cref="RecycleBinReach"/>).</para>
/// </summary>
public interface IRecycleBin
{
    /// <param name="path">
    /// Fully qualified, fully resolved, and without the extended-length prefix. The caller
    /// normalises; this does not, because a seam that silently corrected its input would make the
    /// §6.3 assertion above unfalsifiable.
    /// </param>
    RecycleOutcome Recycle(string path);

    /// <summary>
    /// Why the bin cannot take the item at <paramref name="path"/> as things are now, or null where
    /// nothing shows it cannot. The answer <see cref="Recycle"/> refuses on, asked before a
    /// confirmation so it promises the bin only for what the bin will take; <see cref="Recycle"/>
    /// asks again, because the item or the bin can change while the user reads.
    /// </summary>
    /// <param name="path">In the form <see cref="Recycle"/> takes.</param>
    string? WhyItCannotTake(string path);
}

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
/// <para>Stateless, so one instance serves the process (G5). Each call makes and releases its own
/// COM objects, because a shell operation object is single-use once
/// <c>PerformOperations</c> has run.</para>
/// </summary>
public sealed class ShellRecycleBin : IRecycleBin
{
    public static ShellRecycleBin Default { get; } = new(Perform, RecycleBinReach.Default);

    // FOF_SILENT | FOF_NOCONFIRMATION | FOF_ALLOWUNDO | FOF_NOERRORUI, with FOFX_RECYCLEONDELETE
    // and FOFX_EARLYFAILURE.
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
    // FOFX_EARLYFAILURE stops at the first refusal rather than carrying on. The caller recycles one
    // item per call, so what it buys is that a failure is reported as one rather than swallowed into
    // an aborted flag.
    private const uint OperationFlags =
        0x0004 | 0x0010 | 0x0040 | 0x0400 | 0x00080000 | 0x00100000;

    private static readonly Guid FileOperationClass = new("3ad05575-8857-4850-9277-11b85bdb8e09");

    private readonly Func<string, RecycleOutcome> _perform;
    private readonly RecycleBinReach _reach;

    /// <param name="perform">
    /// The shell's move of one item. A seam, so a test can show that an item the bin cannot take
    /// never reaches the shell without asking the shell to delete anything.
    /// </param>
    /// <param name="reach">What the bin can take, asked before the shell is.</param>
    internal ShellRecycleBin(Func<string, RecycleOutcome> perform, RecycleBinReach reach)
    {
        _perform = perform;
        _reach = reach;
    }

    public string? WhyItCannotTake(string path) => _reach.WhyNot(path);

    public RecycleOutcome Recycle(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (WhyItCannotTake(path) is { } cannotTake)
        {
            return new RecycleOutcome(Removed: false, $"{cannotTake} Deguffer did not ask Windows to move it, and it is still where it was.");
        }

        // The shell's apartment requirement, met on a thread of our own rather than by initialising
        // whatever thread the caller arrived on. Core is called from a thread-pool thread by every
        // caller it has, and CoInitialize on one of those outlives the call — it would change the
        // apartment of a thread the runtime hands to something else next.
        RecycleOutcome outcome = new(Removed: false, "The Recycle Bin operation did not run.");

        var thread = new Thread(() => outcome = _perform(path));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return outcome;
    }

    private static RecycleOutcome Perform(string path)
    {
        object? operation = null;

        try
        {
            var itemId = typeof(IShellItem).GUID;
            ShellNative.SHCreateItemFromParsingName(path, IntPtr.Zero, ref itemId, out var item);

            operation = Activator.CreateInstance(Type.GetTypeFromCLSID(FileOperationClass)!);
            var file = (IFileOperation)operation!;

            var binned = new BinnedItem();

            file.SetOperationFlags(OperationFlags);
            file.Advise(binned, out _);
            file.DeleteItem(item, IntPtr.Zero);
            file.PerformOperations();
            file.GetAnyOperationsAborted(out var aborted);

            return Outcome(aborted, binned);
        }
        catch (COMException ex)
        {
            // Every refusal the shell has arrives this way: a path it will not parse, a volume with
            // no Recycle Bin, an item too large for the bin's quota, and a file something else holds
            // open. They are told apart by HRESULT and none of them is this code's to fix, so the
            // number goes in the message rather than being mapped to a guess.
            return new RecycleOutcome(
                Removed: false,
                $"Windows would not move this to the Recycle Bin (0x{ex.HResult:X8}). It is still where it was.");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidCastException)
        {
            // A path the shell's own parser rejected before it got as far as an HRESULT, or a
            // machine where the file-operation class is not registered.
            return new RecycleOutcome(
                Removed: false,
                $"Windows would not move this to the Recycle Bin: {ex.Message} It is still where it was.");
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
    /// What a finished operation did, as the progress sink saw it. An outright deletion is answered
    /// first, because whatever else the operation says, the item is gone and not in the bin.
    /// </summary>
    internal static RecycleOutcome Outcome(bool aborted, BinnedItem binned) =>
        binned.DeletedOutright
            ? new RecycleOutcome(
                Removed: true,
                "Windows deleted this outright rather than moving it to the Recycle Bin, so it cannot be restored from there.")
            {
                DeletedOutright = true,
            }
            : aborted
                ? new RecycleOutcome(Removed: false, "Windows stopped before moving this to the Recycle Bin.")
                : new RecycleOutcome(Removed: true) { Binned = binned.Path };
}
