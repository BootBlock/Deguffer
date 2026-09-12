namespace Deguffer.Core.Exploring.Knowledge;

/// <summary>
/// Outlook's mail stores, and the folders Outlook keeps them in.
///
/// <para>These have the job <see cref="SharedItems"/>' entries have. §9 excludes them, Explore refuses
/// them and no provider targets them, so a reader looking at the largest file on a business machine is
/// owed what it is and the supported way to make it smaller rather than only a refusal. The
/// <c>.ost</c> needs that most: advice to delete one is everywhere, and the reason it is wrong is not
/// visible from the file.</para>
///
/// <para>Grounded in Microsoft's own pages: "Plan and configure Cached Exchange Mode" for the
/// offline copy's location, its fifty-gigabyte default ceiling and its size against the mailbox;
/// "MRM doesn't process items in the Sync Issues folder" for the folder that is never copied to the
/// server; "Introduction to Outlook data files" for what a <c>.pst</c> holds and where new ones are
/// saved; and "Reduce the size of Outlook data files" for Compact Now.</para>
/// </summary>
internal static class OutlookItems
{
    private const string CompactNow =
        "Outlook's 'Compact Now', in the data file's own settings, is the supported way to make it smaller.";

    public static IReadOnlyList<KnownItem> All { get; } =
    [
        new(
            KnownPlace.AnywhereByExtension,
            ".ost",
            "Outlook's offline copy of an Exchange, Microsoft 365 or Outlook.com mailbox, which is what "
            + "lets Outlook go on working without a connection. Microsoft puts it at 50 to 80 per cent "
            + "larger than the mailbox itself and lets it grow to 50 GB by default, so it is often the "
            + "largest single file on the machine. Most of it is a copy of what is on the server, but "
            + "not all: the items Outlook could not synchronise are kept only in this file.",

            "Advice to delete it is common and Deguffer does not follow it, because only Outlook can "
            + "tell whether it holds mail kept nowhere else — its 'Mail to keep offline' setting is the "
            + "supported way to make it smaller."),

        new(
            KnownPlace.AnywhereByExtension,
            ".pst",
            "An Outlook data file. A POP or IMAP account keeps all of its mail in one, an archive is "
            + "one, and items moved off a mail server to keep the mailbox small end up in one. Unlike "
            + "the offline copy of a mailbox it is not a copy of anything, and Outlook opens it from "
            + "wherever it was saved.",

            "It is usually the only copy of what is in it. " + CompactNow),

        new(
            KnownPlace.LocalAppData,
            @"Microsoft\Outlook",
            "Outlook's own folder for this account: the offline copy of each mailbox, the address books "
            + "Outlook downloads to go with them, and on older versions of Outlook the data files as "
            + "well.",

            "Nothing here goes as a whole, because an offline copy can hold mail kept nowhere else and "
            + "a data file usually is the only copy."),

        new(
            KnownPlace.Anywhere,
            "Outlook Files",
            "The folder Outlook saves new data files in, inside Documents: archives, and the mail of POP "
            + "and IMAP accounts. It is found by name wherever Documents has been moved to.",

            "What is in it is usually the only copy of that mail, and Outlook's 'Compact Now', in each "
            + "data file's own settings, is the supported way to make one smaller."),
    ];
}
