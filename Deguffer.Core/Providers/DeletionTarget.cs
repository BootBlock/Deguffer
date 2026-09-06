namespace Deguffer.Core.Providers;

/// <summary>Which kind of removal a target becomes.</summary>
public enum TargetKind
{
    Directory,
    File,

    /// <summary>
    /// One account's Recycle Bin on one volume, emptied through Windows rather than by deleting its
    /// files. See <see cref="Execution.EmptyRecycleBinStep"/>.
    /// </summary>
    RecycleBin,
}

/// <summary>
/// One path a provider has decided to delete, before anything has been measured.
///
/// Exists so that measuring a set of targets and turning them into steps stays one piece of code
/// rather than one per provider. The correlation between a target and its size is positional, and
/// four copies of the same indexed loop is four chances to pair a path with the wrong number.
/// </summary>
/// <param name="Path">The path, in display form — a plan never holds an extended-length path.</param>
/// <param name="Reason">Why it is disposable, written for the user.</param>
/// <param name="LastWritten">
/// §7's age, where the provider can tell. Null for a whole-cache target: a single timestamp
/// spanning everything a tool ever cached is a number with nothing to mean.
/// </param>
/// <param name="Kind">
/// Which step this becomes. A file is not a small tree — it cannot partially succeed — and a
/// Recycle Bin is emptied by Windows rather than by us, so each is removed by different code and
/// the provider says which it meant.
/// </param>
/// <param name="RequiresElevation">
/// Whether removing this needs administrator rights. A declaration about the location, carried onto
/// the step so a plan can say plainly what it can see and cannot remove.
/// </param>
public readonly record struct DeletionTarget(
    string Path,
    string Reason,
    DateTime? LastWritten = null,
    TargetKind Kind = TargetKind.Directory,
    bool RequiresElevation = false);
