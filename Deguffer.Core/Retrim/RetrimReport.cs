using Deguffer.Core.Scanning;

namespace Deguffer.Core.Retrim;

/// <summary>
/// What the retrim offer and its results say. Every sentence is here, so the page and its tests read
/// one wording.
/// </summary>
public static class RetrimReport
{
    /// <summary>
    /// Windows' own description of each failure <c>MSFT_Volume.Optimize</c> documents, by its return
    /// value, quoted from Microsoft's page for the method.
    /// </summary>
    private static readonly Dictionary<int, string> DocumentedFailures = new()
    {
        [2] = "Unspecified Error",
        [3] = "Timeout",
        [4] = "Failed",
        [5] = "Invalid Parameter",
        [7] = "This command is not supported on x86 running in x64 environment.",
    };

    /// <summary>
    /// The offer, a sentence for each volume and then what a retrim is. Says, where Deguffer is not an
    /// administrator, that accepting it restarts Deguffer as one.
    /// </summary>
    public static string Offer(IReadOnlyList<RetrimCandidate> candidates, bool isElevated)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var lines = candidates.Select(Why).ToList();

        lines.Add("A retrim is the step Windows' own Optimize Drives takes on such a drive. It moves no files, and "
            + "Deguffer never defragments.");

        if (!isElevated)
        {
            lines.Add("Windows retrims only for an administrator, so Deguffer restarts as administrator first and "
                + "retrims when it opens.");
        }

        return string.Join(" ", lines);
    }

    /// <summary>What the button that accepts the offer says.</summary>
    public static string Action(IReadOnlyList<RetrimCandidate> candidates, bool isElevated)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        return !isElevated ? "Restart as administrator and retrim"
            : candidates.Count == 1 ? $"Retrim {Shown(candidates[0].Volume.RootPath)}"
            : $"Retrim {candidates.Count} drives";
    }

    /// <summary>What the page says where the user dismissed Windows' prompt for administrator rights.</summary>
    public const string NotRestarted = "Deguffer was not restarted as administrator, so nothing was retrimmed.";

    /// <summary>What the page says where the run was stopped before every drive was asked.</summary>
    public const string Cancelled = "The retrim was stopped before every drive was retrimmed.";

    /// <summary>What the page says while Windows retrims <paramref name="root"/>.</summary>
    public static string Underway(string root) => $"Windows is retrimming {Shown(root)}…";

    /// <summary>What became of the retrim of <paramref name="root"/>, in Windows' own terms.</summary>
    public static string Result(string root, RetrimAnswer answer)
    {
        var drive = Shown(root);

        return answer.Outcome switch
        {
            RetrimOutcome.Retrimmed => $"Windows retrimmed {drive}.",
            RetrimOutcome.NotSupported => $"Windows said {drive} does not support a retrim.",
            RetrimOutcome.NeedsElevation => $"Windows would not retrim {drive} without administrator rights.",
            RetrimOutcome.Failed when DocumentedFailures.TryGetValue(answer.Code, out var text) =>
                $"Windows could not retrim {drive}. It answered \"{text.TrimEnd('.')}\" (code {answer.Code}).",
            RetrimOutcome.Failed =>
                $"Windows could not retrim {drive}. It answered with code {answer.Code}, which it does not document.",
            RetrimOutcome.Unanswered =>
                $"Windows' storage management did not answer for {drive} (error 0x{answer.Code:X8}), so it was not retrimmed.",
            RetrimOutcome.NotListed => $"Windows' storage management does not list {drive}, so it was not retrimmed.",
            RetrimOutcome.NotOffered => $"{drive} is no longer a drive a retrim is offered on, so it was not retrimmed.",
            _ => throw new ArgumentOutOfRangeException(nameof(answer), answer.Outcome, null),
        };
    }

    /// <summary>A mount point as the page names it: <c>C:</c> for a drive's root, and a folder mount whole.</summary>
    public static string Shown(string root) =>
        root is [_, ':', '\\'] ? root[..2] : root;

    private static string Why(RetrimCandidate candidate)
    {
        var freed = FreeSpace.Format(candidate.Freed);
        var drive = Shown(candidate.Volume.RootPath);

        return candidate.Basis switch
        {
            RetrimBasis.ThinlyProvisioned =>
                $"The clean freed {freed} on {drive}, which is thinly provisioned. The storage behind it takes that "
                + "space back only when Windows retrims the drive, which it otherwise does on a schedule that can be "
                + "days away.",
            RetrimBasis.VirtualDisk =>
                $"The clean freed {freed} on {drive}, which is in a virtual disk. Where the disk grows with what it "
                + "holds, the drive its file is on gets that space back only when Windows retrims "
                + $"{drive}, which it otherwise does on a schedule.",
            RetrimBasis.SolidState =>
                $"The clean freed {freed} on {drive}, which is solid state. Windows tells the drive which blocks "
                + "are free on a schedule, and a retrim tells it now.",
            _ => throw new ArgumentOutOfRangeException(nameof(candidate), candidate.Basis, null),
        };
    }
}
