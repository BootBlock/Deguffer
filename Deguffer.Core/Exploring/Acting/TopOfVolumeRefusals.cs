using Deguffer.Core.Safety;

namespace Deguffer.Core.Exploring.Acting;

/// <summary>
/// What the top of a volume holds that Explore refuses, together with everything in it: NTFS's own
/// records, every Recycle Bin, and what Windows keeps there (<see cref="VolumeReservations"/>).
///
/// <para>One question, asked of where a path sits below its volume's root, and answered the same
/// way for <see cref="ExploreActionPolicy.MayRemove"/> and
/// <see cref="ExploreActionPolicy.RefusedAtAndBelow"/>, which both ask it first. It reads nothing the
/// policy holds, no region and no tool root, so it stands apart from the policy that asks it, and
/// apart from <see cref="VolumeReservations"/>, which is Windows' list of names and not the rule
/// that reads every place an item is reachable at.</para>
/// </summary>
internal static class TopOfVolumeRefusals
{
    private static readonly char[] Separators =
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    /// <summary>
    /// The names NTFS reserves in a volume's root directory, from <c>[MS-FSCC]</c>. See
    /// <see cref="ReservedByTheFilesystem"/> for why they are refused and why the set stops here.
    /// </summary>
    private static readonly HashSet<string> NtfsReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "$MFT", "$MFTMirr", "$LogFile", "$Volume", "$AttrDef", "$Bitmap",
        "$Boot", "$BadClus", "$Secure", "$UpCase", "$Extend",
    };

    /// <summary>
    /// What Windows and NTFS keep at the top of a volume, and every Recycle Bin, read on every
    /// reading of every path the item is reachable at. Each refuses a name and everything in it.
    /// </summary>
    public static ExploreVerdict? Refusal(IReadOnlyList<VolumePlace> places)
    {
        IReadOnlyList<string> readings = [.. places.SelectMany(place => place.Readings)];

        return OnAnyReading(readings, ReservedByTheFilesystem)
            ?? OnAnyReading(readings, InARecycleBin)
            ?? OnAnyReading(readings, VolumeReservations.Refusal);
    }

    /// <summary>
    /// NTFS's own records, which §7.1 puts out of reach: they are live filesystem state, so the tier
    /// model calls them Tier 4, and Explore "refuses whatever the tier model would call Tier 4, and
    /// it does not get to decide what that is".
    ///
    /// <para>Separate from <see cref="VolumeReservations"/> and not folded into it, because the two
    /// sets come from different owners. What Windows keeps at the top of a volume changes with
    /// Windows, and this set is closed by the filesystem's specification. Both ask about the first
    /// segment below the root, because NTFS's optional features live a level down in
    /// <c>$Extend</c>.</para>
    ///
    /// <para>They are refused at all because §5.5's file-table route <em>draws</em> them. A walk
    /// never sees these names — Windows hides the reserved records from directory enumeration — but
    /// reading the table directly puts <c>$MFT</c> at the top of a scanned drive at several hundred
    /// megabytes, which is exactly the shape of thing a size picture invites somebody to act on.
    /// Offering a deletion the filesystem will refuse teaches a user that saying yes is how you find
    /// out what happens, and §7.1 wants the reason stated instead.</para>
    ///
    /// <para>The set is closed and comes from the filesystem's own specification rather than from
    /// observation, so it needs no maintenance: <c>[MS-FSCC]</c> names what NTFS reserves in a
    /// volume's root directory. It is deliberately <em>not</em> every name beginning with <c>$</c>.
    /// <c>$Recycle.Bin</c> is Windows' rather than NTFS's and is refused below for its own reason,
    /// and <c>$WinREAgent</c> and <c>$Windows.~BT</c> are ordinary leftovers a user may legitimately
    /// want gone — refusing those would take away a capability rather than add a protection.</para>
    /// </summary>
    private static ExploreVerdict? ReservedByTheFilesystem(string below) =>
        below.Split(Separators, StringSplitOptions.RemoveEmptyEntries) is [var first, ..]
        && NtfsReserved.Contains(first)
            ? ExploreVerdict.Refuse(
                $"'{first}' is part of NTFS itself rather than something stored on the drive — it is "
                + "how the filesystem records where every other file is. Windows does not let it be "
                + "deleted, and the space it holds is not recoverable while the drive is in use.")
            : null;

    /// <summary>
    /// A volume's Recycle Bin and everything in it.
    ///
    /// <para>Everything in it, not only the folder, because the bin holds a folder for each account
    /// that has deleted something on the drive. <see cref="Providers.RecycleBinProvider"/> empties
    /// this user's own and names every other one as a path that must survive, and §7.1 refuses every
    /// such path. Refusing the bin alone left another account's deleted files one level down, and
    /// removable wherever Deguffer runs elevated. This user's own is refused too: the Storage page is
    /// where it is emptied, and a deleted file is two entries there, its contents and the record of
    /// where it came from, so removing either leaves a file the bin cannot put back.</para>
    ///
    /// <para>By the first segment below the volume root, as <see cref="ReservedByTheFilesystem"/>
    /// asks, so a folder somebody named <c>$Recycle.Bin</c> inside their own documents stays
    /// theirs.</para>
    /// </summary>
    private static ExploreVerdict? InARecycleBin(string below) =>
        below.Split(Separators, StringSplitOptions.RemoveEmptyEntries) is [var first, ..]
        && first.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
            ? ExploreVerdict.Refuse(
                "This is the drive's Recycle Bin, where each account on this computer keeps what it "
                + "deleted. Emptying yours is offered on the Storage page, where Deguffer can tell your "
                + "own deleted files from another account's.")
            : null;

    private static ExploreVerdict? OnAnyReading(
        IReadOnlyList<string> readings, Func<string, ExploreVerdict?> rule)
    {
        foreach (var below in readings)
        {
            if (rule(below) is { } refusal)
            {
                return refusal;
            }
        }

        return null;
    }
}
