using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// What a tool that removes an item later did to it in the time it was given, once its command reported
/// success. See <see cref="ScheduledRemoval"/>.
///
/// <para><b>Looked for rather than trusted.</b> <c>lms runtime remove</c> prints that it removed
/// the runtime and exits zero once LM Studio has accepted the request, before LM Studio has
/// written anything, and a failure after that point never reaches the command. The marker is the
/// only evidence the removal will happen.</para>
/// </summary>
internal static class RemovalMarking
{
    /// <summary>
    /// How long a command's tool is given to mark the item it will remove later. LM Studio answers
    /// <c>lms</c> before it writes its marker, so the first look can come too early, and the write
    /// itself takes milliseconds. Long enough for a machine under load, and short enough that a tool
    /// that never marks anything costs the run a few seconds rather than a hang.
    /// </summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    /// <summary>What the tool did to <paramref name="item"/>, looked for until <see cref="Wait"/> has passed on <paramref name="time"/>.</summary>
    public static async Task<RemovalMark> AwaitAsync(string item, ScheduledRemoval scheduled, TimeProvider time, CancellationToken ct)
    {
        var marker = Path.Combine(item, scheduled.Marker);
        var deadline = time.GetUtcNow() + Wait;

        while (true)
        {
            // The entry probe, because the directory probe answers Absent for a file at the path too.
            if (LongPath.ProbeEntry(item) is PathPresence.Absent)
            {
                return RemovalMark.Removed;
            }

            switch (LongPath.ProbeFile(marker))
            {
                case PathPresence.Present:
                    return RemovalMark.Marked;

                // Not polled further: waiting does not make Windows describe it, and reaching the
                // deadline would say the tool did nothing, which nobody saw.
                case PathPresence.Refused:
                    return RemovalMark.Unseen;
            }

            if (time.GetUtcNow() >= deadline)
            {
                return RemovalMark.Neither;
            }

            await Task.Delay(Poll, time, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>What a tool that removes later was seen to do. See <see cref="RemovalMarking"/>.</summary>
internal enum RemovalMark
{
    Marked,
    Removed,
    Neither,

    /// <summary>Windows would not describe the marker, so whether the tool wrote it is unknown.</summary>
    Unseen,
}
