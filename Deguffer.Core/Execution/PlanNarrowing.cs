using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// The plans a run executes, narrowed to the steps the user ticked, with what each declined subject
/// held asked of the disk when Clean is pressed (<see cref="ChosenSteps"/>).
///
/// <para><b>Off the UI thread, and several subjects at once</b> (<see cref="DiskBoundWork"/>). A row
/// offers one step per item and some rows offer thousands, so a user who unticks most of a large row
/// makes thousands of content walks, which take seconds on a cold or spinning disk. Every declined
/// subject of every row is one list, worked under each volume's walk tuning and the shared listing
/// budget, so the walks share a disk with whatever else is reading it, and the window that pressed
/// Clean goes on answering.</para>
///
/// <para><b>Each subject records what asking it alone records.</b> Every declined subject is protected
/// with its own answer, in the order its plan gives its steps.</para>
/// </summary>
public sealed class PlanNarrowing
{
    private readonly ScanTuner _tuning;
    private readonly Func<string, bool> _content;

    /// <param name="tuning">What the walks of each volume run with. See <see cref="IDirectoryScanner.Tuning"/>.</param>
    public PlanNarrowing(ScanTuner tuning)
        : this(tuning, DirectoryContent.IsPresent)
    {
    }

    /// <param name="content">The content question asked of each declined subject: the seam a test blocks or counts the walks through.</param>
    internal PlanNarrowing(ScanTuner tuning, Func<string, bool> content)
    {
        _tuning = tuning;
        _content = content;
    }

    /// <summary>Each of <paramref name="choices"/> narrowed to its chosen steps, in their order.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<IReadOnlyList<CleanupPlan>> NarrowAsync(IReadOnlyList<ChosenSteps> choices, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(choices);

        var held = await DiskBoundWork.EachAsync(
            [.. choices.SelectMany(choice => choice.DeclinedSubjects)],
            _tuning,
            _content,
            ct).ConfigureAwait(false);

        var narrowed = new CleanupPlan[choices.Count];
        var answers = held as bool[] ?? [.. held];
        var start = 0;

        for (var index = 0; index < choices.Count; index++)
        {
            var count = choices[index].DeclinedSubjects.Count;
            narrowed[index] = choices[index].Narrowed(answers.AsSpan(start, count));
            start += count;
        }

        return narrowed;
    }
}
