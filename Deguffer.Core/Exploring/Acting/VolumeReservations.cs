namespace Deguffer.Core.Exploring.Acting;

/// <summary>
/// What Windows keeps at the top of a volume that Explore refuses, each with the reason the user is
/// shown. The counterpart of <see cref="Knowledge.VolumeItems"/>, which explains the same names on
/// hover and decides nothing.
///
/// <para>Data rather than decisions, as <see cref="ProtectedRegions"/> is, which is why it stands
/// apart from the policy that asks it. The policy decides <em>where</em> the top of a volume is, from
/// where the volume is mounted at the moment of the question, and this says which names there are
/// refused.</para>
///
/// <para><b>Every name covers what is inside it.</b> Removing a folder removes everything in it, and
/// a rule that refused <c>Boot</c> while allowing <c>Boot\BCD</c> would refuse the folder and allow
/// the one file in it that the machine cannot start without.</para>
///
/// <para>They are named because Explore draws them. The paging files and the restore points are
/// among the largest items on a drive, so they are exactly what a size picture puts in front of
/// somebody, and "access denied" from a deletion the app offered is a worse answer than not offering
/// it. The boot files are small and are here for the opposite reason: the guide says a machine
/// without them does not start, and moving them to the Recycle Bin leaves nothing that can start to
/// restore them. <c>Recovery</c> is also a §5.6 survivor of <see cref="Providers.SystemDriveRoot"/>,
/// and §7.1 refuses every path a provider names as protected.</para>
///
/// <para>A name the guide describes and this does not refuse is allowed on purpose, and
/// <c>VolumeReservationTests</c> lists each of them, so a guide entry saying an item must stay cannot
/// be added without deciding here whether Explore refuses it.</para>
/// </summary>
internal static class VolumeReservations
{
    private static readonly char[] Separators =
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    private static readonly Dictionary<string, string> Reasons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["System Volume Information"] =
            "Windows keeps this drive's restore points, indexing data and change journal here. "
            + "It belongs to the operating system, and Windows is what should reclaim it.",

        ["pagefile.sys"] = Managed("the paging file"),
        ["swapfile.sys"] = Managed("the swap file"),
        ["hiberfil.sys"] = Managed("the hibernation file"),

        ["bootmgr"] =
            "This is the Windows Boot Manager, the file the firmware starts on a machine that starts "
            + "through a BIOS. A machine without it does not start at all, so nothing moved to the "
            + "Recycle Bin could be put back.",

        ["Boot"] =
            "This is the boot configuration, which says what the machine can start and how. A machine "
            + "without it does not start at all, so nothing moved to the Recycle Bin could be put back.",

        ["Recovery"] =
            "This is the Windows recovery environment, which starts when Windows cannot and runs "
            + "'Reset this PC' and Startup Repair. Removing it takes those away without any sign until "
            + "the day they are needed.",

        ["Documents and Settings"] =
            "This is a signpost Windows keeps so that older software finds the Users folder where it "
            + "expects it. It holds nothing of its own, and removing it breaks that software.",
    };

    /// <summary>
    /// The refusal for a path whose first segment below its volume's root is one of the names here,
    /// or null where it is not.
    /// </summary>
    /// <param name="below">Where the path sits below its volume's root, from <see cref="Safety.VolumeRoot"/>.</param>
    public static ExploreVerdict? Refusal(string below) =>
        below.Split(Separators, StringSplitOptions.RemoveEmptyEntries) is [var first, ..]
        && Reasons.TryGetValue(first, out var reason)
            ? ExploreVerdict.Refuse(reason)
            : null;

    private static string Managed(string what) =>
        $"This is {what}. Windows manages it, and its size is changed through the system settings "
        + "rather than by deleting it.";
}
