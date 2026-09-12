namespace Deguffer.Core.Memory;

/// <summary>
/// Which documented source gives Deguffer's own private working set, and what the fallback source's
/// answer comes to. See <see cref="OwnProcessCounters"/> for the two calls.
/// </summary>
internal static class OwnPrivateWorkingSet
{
    /// <summary><c>PSAPI_WORKING_SET_BLOCK.Shared</c>: the ninth bit of each entry.</summary>
    private const nuint SharedPage = 0x100;

    /// <summary>
    /// The counter's figure where it gave one, and otherwise whatever the walk finds.
    ///
    /// <para>Zero counts as no answer. A running process always has private pages in memory, so a zero
    /// is a counter this Windows did not fill rather than a figure, and treating it as one would turn
    /// the check off on exactly the Windows the fallback exists for.</para>
    /// </summary>
    /// <param name="counterAnswered">Whether the call reading the counter succeeded.</param>
    /// <param name="counterFigure">What it left in <c>PrivateWorkingSetSize</c>.</param>
    /// <param name="walk">The fallback, run only where the counter did not answer.</param>
    public static long? Choose(bool counterAnswered, ulong counterFigure, Func<long?> walk) =>
        counterAnswered && counterFigure > 0 ? (long)counterFigure : walk();

    /// <summary>
    /// The private bytes in a <c>PSAPI_WORKING_SET_INFORMATION</c>: the entry count first, then one entry
    /// per page, of which the pages Windows does not mark shareable are private.
    ///
    /// <para>A count larger than the buffer holds is read only as far as the buffer goes, rather than
    /// past it.</para>
    /// </summary>
    public static long PrivateBytes(ReadOnlySpan<nuint> information, long pageSize)
    {
        var count = (int)Math.Min(information[0], (nuint)(information.Length - 1));
        long privatePages = 0;

        foreach (var entry in information.Slice(1, count))
        {
            if ((entry & SharedPage) == 0)
            {
                privatePages++;
            }
        }

        return privatePages * pageSize;
    }
}
