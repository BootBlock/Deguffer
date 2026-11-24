namespace Deguffer.Core.Text;

/// <summary>
/// The word after a setting's number box, so the box and its unit read as one phrase ("8 hours",
/// "1 day") and agree as the number changes. A number box holds a <see cref="double"/>, and an empty
/// one holds <see cref="double.NaN"/> and shows its placeholder, so a unit beside an empty box agrees
/// with the count the placeholder names.
/// </summary>
public static class SettingUnit
{
    public static string Hours(double shown) => Agreeing(shown, "hour", "hours");

    public static string Days(double shown) => Agreeing(shown, "day", "days");

    /// <param name="whenEmpty">The count an empty box stands for, which its placeholder names.</param>
    public static string Threads(double shown, int whenEmpty) => Agreeing(Count(shown, whenEmpty), "thread", "threads");

    /// <param name="whenEmpty">The count an empty box stands for, which its placeholder names.</param>
    public static string Reads(double shown, int whenEmpty) => Agreeing(Count(shown, whenEmpty), "read", "reads");

    private static double Count(double shown, int whenEmpty) => double.IsNaN(shown) ? whenEmpty : shown;

    /// <summary>
    /// A box holds whole numbers, as each setting's entry rounds what was typed, so the count it shows
    /// is its value as a whole number. An empty box with no count of its own reads as none.
    /// </summary>
    private static string Agreeing(double count, string one, string many) =>
        Counted.Form(long.CreateSaturating(count), one, many);
}
