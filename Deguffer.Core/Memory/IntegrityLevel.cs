namespace Deguffer.Core.Memory;

/// <summary>
/// Whether one process's integrity level is above Deguffer's own, which §7.2.1 refuses on because
/// Windows may drop a posted message without saying so.
///
/// <para><b>Compared by value, never by named level.</b> Windows states that integrity levels are an
/// ordered scale of numbers — "a lower value indicates a lower integrity level" — and hands a
/// UIAccess application "the value of medium integrity level, plus an increment of 0x10"
/// (<see href="https://learn.microsoft.com/en-us/previous-versions/dotnet/articles/bb625963(v=msdn.10)">Windows
/// Integrity Mechanism Design</see>). That increment exists to rank such a process above plain medium,
/// so a comparison that grouped both into a "medium" band would call them equal and let Deguffer post
/// to a window the filter blocks. Comparing the values ranks a level between two named ones correctly,
/// which is what §7.2.1 asks of this, and refuses wherever a band comparison would.</para>
///
/// <para>The levels themselves are the last subauthority of the token's mandatory label, which
/// Microsoft's own sample reads the same way
/// (<see href="https://learn.microsoft.com/en-us/windows/win32/secauthz/well-known-sids">Well-known
/// SIDs</see>): untrusted 0x0, low 0x1000, medium 0x2000, medium high 0x2100, high 0x3000, system
/// 0x4000, protected 0x5000.</para>
/// </summary>
internal static class IntegrityLevel
{
    /// <summary>
    /// Whether <paramref name="target"/> is above <paramref name="own"/>, and
    /// <see cref="Answer.Unreadable"/> where either would not be read: a level Windows would not say is
    /// a level Deguffer cannot post below.
    /// </summary>
    public static Answer Above(int? target, int? own) =>
        target is not { } level || own is not { } ours ? Answer.Unreadable
        : level > ours ? Answer.Yes
        : Answer.No;
}
