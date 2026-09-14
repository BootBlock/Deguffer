namespace Deguffer.Core.Memory.Acting;

/// <summary>
/// Whether Memory will ask one program to close, and what to tell the user either way (§7.2.1).
///
/// <para>A record rather than a bare bool for the reason <see cref="Exploring.Acting.ExploreVerdict"/>
/// is one, and §7.2.1 states it again for this subject: <b>a refusal is a sentence on the row, never
/// a disabled button.</b> A picture of memory draws every process on the machine, and a user who
/// picked one and found nothing to press would learn nothing about which row of that table
/// applied.</para>
///
/// <para>It never says a program is safe to close, idle or unneeded, and it never suggests closing
/// anything (§7.2). What it says of a program it would act on is what the action does.</para>
/// </summary>
/// <param name="IsAllowed">Whether the close may be offered.</param>
/// <param name="Reason">The sentence shown on the row, refused or not.</param>
/// <param name="Windows">
/// The windows the close would be posted to, in the order Windows enumerated them, and empty on a
/// refusal. The confirmation names how many there are, because closing one window of a program that
/// has several does not close the program.
/// </param>
public sealed record MemoryVerdict(bool IsAllowed, string Reason, IReadOnlyList<ProcessWindow> Windows)
{
    public static MemoryVerdict Refuse(string reason) => new(IsAllowed: false, reason, []);

    public static MemoryVerdict Allow(IReadOnlyList<ProcessWindow> windows) => new(
        IsAllowed: true,
        "Deguffer can ask this program to close, the way its own close button does. The program "
        + "decides what happens to anything unsaved, and it may refuse.",
        windows);
}
