namespace Deguffer.Core.Health.Smartmontools;

/// <summary>
/// The message the installed smartmontools drive database gives with the entry for a drive's family,
/// such as a firmware fault that can hang or corrupt the drive and the update that fixes it. It is the
/// database's text, shown as written: it is shown wherever the entry is used, as smartctl shows it,
/// because whether this drive's firmware is the one it names is in prose Deguffer does not interpret.
/// It is not a reading of the drive, so it never changes the verdict.
/// </summary>
/// <param name="Family">The family of the entry that gives it.</param>
/// <param name="Message">The database's text, its lines as the database breaks them.</param>
public sealed record DatabaseWarning(string Family, string Message)
{
    public string Heading => $"The smartmontools drive database warns about {Family}";

    /// <summary>The warning an entry gives, or null where its message is empty.</summary>
    internal static DatabaseWarning? Of(string family, string message) =>
        message.Trim() is { Length: > 0 } text ? new DatabaseWarning(family, text) : null;
}
