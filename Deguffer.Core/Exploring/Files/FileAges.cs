namespace Deguffer.Core.Exploring.Files;

/// <summary>What each <see cref="FileAge"/> means against a calendar, and what it is called.</summary>
public static class FileAges
{
    /// <summary>The ages in the order a picker lists them, built once (G5).</summary>
    public static IReadOnlyList<FileAge> All { get; } = Enum.GetValues<FileAge>();

    /// <summary>
    /// The newest write a file may have and still be listed, or null for <see cref="FileAge.Any"/>.
    ///
    /// <para>Calendar months and years rather than a fixed count of days, so "a year" is the same
    /// date last year whatever the leap years did. A file written exactly at the cutoff has gone the
    /// whole period without a write, and is listed.</para>
    /// </summary>
    public static DateTime? Cutoff(FileAge age, DateTime now) => age switch
    {
        FileAge.Any => null,
        FileAge.ThreeMonths => now.AddMonths(-3),
        FileAge.OneYear => now.AddYears(-1),
        FileAge.TwoYears => now.AddYears(-2),
        _ => throw new ArgumentOutOfRangeException(nameof(age), age, null),
    };

    /// <summary>What an age is called beside the words "not written for".</summary>
    public static string Label(FileAge age) => age switch
    {
        FileAge.Any => "Any time",
        FileAge.ThreeMonths => "3 months",
        FileAge.OneYear => "1 year",
        FileAge.TwoYears => "2 years",
        _ => throw new ArgumentOutOfRangeException(nameof(age), age, null),
    };
}
