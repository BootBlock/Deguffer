namespace Deguffer.Core.Startup;

/// <summary>
/// What Memory says about a picked program that Windows starts at sign-in (§7.5): a fact and a link,
/// and never a suggestion that it should not start, for the reason §7.2 never suggests that a program
/// should close.
/// </summary>
/// <param name="Entry">The entry the link opens on Startup: the first of them, where several start one program.</param>
public sealed record StartupLink(StartupEntry Entry, string Sentence)
{
    /// <summary>
    /// The link for a process running <paramref name="imagePath"/>, or null where no entry in
    /// <paramref name="reading"/> starts that program, or Windows would not say which program it runs.
    /// </summary>
    public static StartupLink? For(string? imagePath, StartupReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        if (imagePath is null || reading.Starting(imagePath) is not [var first, ..] entries)
        {
            return null;
        }

        var named = entries.Count == 1
            ? $"'{first.Title}' ({first.Key.Source.Describe()})"
            : $"'{first.Title}' and {entries.Count - 1} more";

        var sentence = entries.Any(e => e.State is StartupState.Starts)
            ? $"Windows starts this program at sign-in, from {named}."
            : entries.All(e => e.State is StartupState.Disabled)
                ? $"Windows does not start this program at sign-in: its startup {(entries.Count == 1 ? "entry" : "entries")}, {named}, {(entries.Count == 1 ? "is" : "are")} disabled."
                : $"This program has a startup entry, {named}, and whether Windows starts it is unknown.";

        return new StartupLink(first, sentence);
    }
}
