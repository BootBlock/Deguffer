using System.Text.Json;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// The Dropbox folders this account's Dropbox desktop app records, read from the <c>info.json</c>
/// Dropbox documents at both <c>%APPDATA%\Dropbox</c> and <c>%LOCALAPPDATA%\Dropbox</c>.
///
/// <para>Each record holds a <c>personal</c> and a <c>business</c> entry, one for each account
/// connected on this computer, and each entry's <c>path</c> is that account's Dropbox folder.
/// Nothing else in the record is read.</para>
///
/// <para><b>A recorded folder is a setting</b>, so it is vetted through
/// <see cref="ConfiguredFolder.WhyNotOwned"/> before anything in it is examined. The cache is reached
/// inside it, and every entry beside the cache is named as a survivor (§5.6), which would read
/// another row's removal from a drive root or the profile as a failure of this one.</para>
/// </summary>
/// <param name="Folders">The Dropbox folders that are there, or may be, once each however they are named.</param>
/// <param name="Records">Each record that is there or may be, which the clean never touches.</param>
/// <param name="DeclinedFolders">Recorded folders Deguffer does not examine as a Dropbox folder.</param>
/// <param name="Notes">What the user is told about a record or a folder left unread or declined.</param>
/// <param name="LeftSomethingUnexamined">
/// Whether a record could not be read, or named a folder that was declined, so a Dropbox folder may
/// exist that was neither cleared nor ruled out.
/// </param>
internal sealed record DropboxFolders(
    IReadOnlyList<DropboxFolder> Folders,
    IReadOnlyList<string> Records,
    IReadOnlyList<string> DeclinedFolders,
    IReadOnlyList<PlanNote> Notes,
    bool LeftSomethingUnexamined)
{
    /// <summary>The desktop app's process, <c>Dropbox.exe</c>.</summary>
    public const string ProcessName = "Dropbox";

    /// <summary>The hidden cache Dropbox keeps at the top of every Dropbox folder.</summary>
    public const string CacheFolder = ".dropbox.cache";

    /// <summary>Far beyond a record of two accounts, which is a few hundred bytes.</summary>
    private const int MaximumRecordBytes = 64 * 1024;

    private const string RecordName = "Dropbox's record of its folders";

    /// <summary>The account entries a record holds, as Dropbox names them.</summary>
    private static readonly IReadOnlyList<string> Accounts = ["personal", "business"];

    /// <summary>Both places Dropbox documents for <c>info.json</c>, roaming first.</summary>
    public static IReadOnlyList<string> RecordPaths(IUserEnvironment environment) =>
    [
        Path.Combine(environment.RoamingAppData, "Dropbox", "info.json"),
        Path.Combine(environment.LocalAppData, "Dropbox", "info.json"),
    ];

    public static DropboxFolders Find(IUserEnvironment environment, ISystemDirectories machine, IVolumeInventory volumes)
    {
        var folders = new List<(DropboxFolder Folder, ReachedFolder Reached)>();
        var records = new List<string>();
        var declinedFolders = new List<string>();
        var notes = new List<PlanNote>();
        var withheld = false;
        IReadOnlyList<string>? temporary = null;

        foreach (var record in RecordPaths(environment))
        {
            var presence = LongPath.ProbeEntry(record, out var isDirectory);

            if (presence is PathPresence.Absent)
            {
                continue;
            }

            records.Add(record);

            if (presence is PathPresence.Present && isDirectory is not true && RecordedFolders(record) is { } recorded)
            {
                foreach (var (account, value) in recorded)
                {
                    Add(account, value);
                }

                continue;
            }

            withheld = true;
            var unread = new UnreadFile(LongPath.Display(record), presence is not PathPresence.Present);

            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"{unread.Opening(RecordName)} as the record Deguffer expects, so a Dropbox folder it names was "
                + "neither cleared nor ruled out."));
        }

        return new DropboxFolders([.. folders.Select(known => known.Folder)], records, declinedFolders, notes, withheld);

        void Add(string account, string value)
        {
            if (LongPath.Configured(value) is not { } named)
            {
                withheld = true;
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Dropbox records '{value}' as your {account} Dropbox folder, and Deguffer leaves it alone: "
                    + ConfiguredFolder.NotAFullPath));

                return;
            }

            // Resolved once for every recorded folder, and only where a record names one.
            temporary ??= TempRoots.Resolve(environment, machine, volumes).AccountFolders;

            if (ConfiguredFolder.WhyNotOwned(named, environment, machine, volumes, temporary) is { } why)
            {
                withheld = true;
                declinedFolders.Add(named);
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Dropbox records '{LongPath.Display(named)}' as your {account} Dropbox folder, and Deguffer "
                    + $"leaves it alone: {why}"));

                return;
            }

            // A record can outlive the folder it names, when Dropbox is unlinked or the folder is
            // moved by hand. That is not a Dropbox folder on this machine, and is passed over.
            if (LongPath.ProbeDirectory(named) is PathPresence.Absent)
            {
                return;
            }

            var reached = ReachedFolder.At(named, volumes);

            if (!folders.Exists(known => known.Reached.IsSameAs(reached)))
            {
                folders.Add((new DropboxFolder(named), reached));
            }
        }
    }

    /// <summary>
    /// The account and <c>path</c> of each entry the record holds, or null where the record could not
    /// be read as one. An entry without a path names no folder, and is passed over.
    /// </summary>
    private static IReadOnlyList<(string Account, string Path)>? RecordedFolders(string record)
    {
        using var document = BoundedJsonFile.Read(record, MaximumRecordBytes);

        if (document is null)
        {
            return null;
        }

        return
        [
            .. Accounts
                .Select(account => (
                    Account: account,
                    Path: document.RootElement.TryGetProperty(account, out var entry) && entry.ValueKind == JsonValueKind.Object
                        ? BoundedJsonFile.StringProperty(entry, "path")
                        : null))
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
                .Select(entry => (entry.Account, entry.Path!)),
        ];
    }
}

/// <summary>One Dropbox folder, which holds the account's files and, at its top, Dropbox's cache.</summary>
/// <param name="Path">The Dropbox folder. Never a target.</param>
internal sealed record DropboxFolder(string Path)
{
    /// <summary>The cache folder, which is emptied and never removed.</summary>
    public string Cache => System.IO.Path.Combine(Path, DropboxFolders.CacheFolder);
}
