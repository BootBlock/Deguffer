using System.Text.Json;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Every folder on this machine that Espressif's ESP-IDF tool installers keep their downloaded archives
/// in, as the tools folders that hold each <c>dist</c>.
///
/// <para><b>Researched from source, not measured.</b> <c>idf_tools.py</c> (espressif/esp-idf,
/// <c>tools/idf_tools.py</c>) downloads every archive into <c>dist</c> inside its tools folder, which is
/// <c>IDF_TOOLS_PATH</c>, or <c>%USERPROFILE%\.espressif</c> where that is not set. The ESP-IDF
/// Installation Manager (espressif/idf-im-ui, <c>src-tauri/src/lib/settings.rs</c>) installs its tools into
/// <c>C:\Espressif\tools</c> and downloads into the <c>dist</c> beside it, <c>C:\Espressif\dist</c>, which is
/// also where the older ESP-IDF Tools Installer kept its archives. It records each ESP-IDF it installs in
/// <c>eim_idf.json</c> in its tools folder, and sets <c>IDF_TOOLS_PATH</c> to the <c>idfToolsPath</c> of
/// that record in the environment it activates, so an <c>idf_tools.py</c> run there downloads into the
/// <c>dist</c> inside that folder. Each recorded folder is examined as well.</para>
///
/// <para><b>The Installation Manager's paths are written in, not read from the system.</b> Its default is
/// <c>C:\Espressif</c> whatever drive Windows is on. It is looked for on the system drive, which is drive C
/// on nearly every machine, so that a test can build it.</para>
///
/// <para><b>A renamed download folder is not looked for.</b> The Installation Manager's
/// <c>tool_download_folder_name</c> setting moves its downloads, and it records the choice nowhere
/// Deguffer can read. Where the Installation Manager is installed, the plan says so rather than search
/// the disk for a folder of archives.</para>
///
/// <para>A folder named by <c>IDF_TOOLS_PATH</c> or by the record is something another program wrote,
/// so it is vetted by <see cref="ConfiguredFolder"/> before anything in it is examined.</para>
/// </summary>
/// <param name="Folders">The tools folders to examine, each once however it is named.</param>
/// <param name="Notes">What the user is told about a folder that was named and withheld, or a record not read.</param>
/// <param name="DeclinedFolders">
/// Folders a setting names that were declined. The installers still write their tools there, so Explore
/// refuses those entries in them.
/// </param>
/// <param name="LeftSomethingUnexamined">Whether a folder was named and withheld rather than examined.</param>
/// <param name="HasInstallationManager">Whether the Installation Manager's record is, or may be, there.</param>
public sealed record EspIdfLayout(
    IReadOnlyList<EspIdfToolsFolder> Folders,
    IReadOnlyList<PlanNote> Notes,
    IReadOnlyList<string> DeclinedFolders,
    bool LeftSomethingUnexamined,
    bool HasInstallationManager)
{
    /// <summary>The variable <c>idf_tools.py</c> reads its tools folder from.</summary>
    public const string ToolsPathVariable = "IDF_TOOLS_PATH";

    /// <summary>The folder inside a tools folder that every archive is downloaded into.</summary>
    public const string DownloadFolder = "dist";

    /// <summary>The folder on the system drive the Installation Manager keeps its tools and downloads in.</summary>
    public const string InstallationManagerFolder = "Espressif";

    /// <summary>The Installation Manager's record of each ESP-IDF it installed, in its tools folder.</summary>
    public const string InstallationRecord = "eim_idf.json";

    /// <summary>The entries the installers write into a tools folder, which Explore refuses in a declined one.</summary>
    public static readonly IReadOnlyList<string> WrittenEntries =
        [DownloadFolder, "tools", "python_env", "frameworks", "idf-env.json", InstallationRecord];

    /// <summary>A record this size is far beyond any number of installations, and is not read past.</summary>
    private const int MaximumRecordBytes = 1024 * 1024;

    private const string DefaultReason =
        "ESP-IDF's tools folder, with the tools it installed and the Python environments it builds with. "
        + "Only the archives in its dist folder are removed.";

    private const string InstallationManagerReason =
        "The ESP-IDF Installation Manager's folder, with the tools it installed and its record of each ESP-IDF. "
        + "Only the archives in its dist folder are removed.";

    private const string VariableReason =
        "The tools folder IDF_TOOLS_PATH names, with the tools ESP-IDF installed there. Only the archives in "
        + "its dist folder are removed.";

    private const string RecordedReason =
        "A tools folder the ESP-IDF Installation Manager recorded for an ESP-IDF it installed. Only the "
        + "archives in its dist folder are removed.";

    private const string RecordName = "the ESP-IDF Installation Manager's record of its installations";

    /// <summary>The tools folder <c>idf_tools.py</c> uses where <c>IDF_TOOLS_PATH</c> is not set.</summary>
    public static string DefaultFolder(IUserEnvironment environment) =>
        Path.Combine(environment.UserProfile, ".espressif");

    /// <summary>The Installation Manager's folder, holding its <c>tools</c> and its <c>dist</c>.</summary>
    public static string InstallationManagerRoot(ISystemDirectories machine) =>
        Path.Combine(machine.SystemDrive, InstallationManagerFolder);

    /// <summary>Where the Installation Manager keeps <see cref="InstallationRecord"/>.</summary>
    public static string InstallationRecordPath(ISystemDirectories machine) =>
        Path.Combine(InstallationManagerRoot(machine), "tools", InstallationRecord);

    public static EspIdfLayout Find(IUserEnvironment environment, ISystemDirectories machine, IVolumeInventory volumes)
    {
        var folders = new List<(EspIdfToolsFolder Folder, ReachedFolder Reached)>();
        var notes = new List<PlanNote>();
        var declinedFolders = new List<string>();
        var withheld = false;
        IReadOnlyList<string>? temporary = null;

        Add(DefaultFolder(environment), DefaultReason);

        var setting = ConfiguredFolder.FromVariable(ToolsPathVariable, DefaultFolder(environment), environment, machine, volumes);

        if (setting.Declined is { } declined)
        {
            withheld = true;
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"{ToolsPathVariable} names '{setting.Named ?? setting.Value}' for ESP-IDF's tools, and Deguffer "
                + $"leaves it alone: {declined}"));

            if (setting.DeclinedFolder is { } folder)
            {
                declinedFolders.Add(folder);
            }
        }
        else if (setting.Folder is { } moved)
        {
            Add(moved, VariableReason);
        }

        Add(InstallationManagerRoot(machine), InstallationManagerReason);

        var record = InstallationRecordPath(machine);
        var hasManager = LongPath.FileMayExist(record);

        if (hasManager)
        {
            if (RecordedToolsFolders(record) is { } recorded)
            {
                foreach (var value in recorded)
                {
                    AddRecorded(value);
                }
            }
            else
            {
                withheld = true;
                var unread = new UnreadFile(LongPath.Display(record), LongPath.ProbeFile(record) is not PathPresence.Present);

                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"{unread.Opening(RecordName)} as the record Deguffer expects, so a tools folder it names was "
                    + "neither cleared nor ruled out."));
            }

            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                "The ESP-IDF Installation Manager can be set to download into a folder of another name, and it "
                + "records that choice nowhere Deguffer can read. Deguffer looks only in the dist folders "
                + "named here, so archives in a renamed folder are not found."));
        }

        return new EspIdfLayout([.. folders.Select(known => known.Folder)], notes, declinedFolders, withheld, hasManager);

        void AddRecorded(string value)
        {
            if (LongPath.Configured(value) is not { } named)
            {
                withheld = true;
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"The ESP-IDF Installation Manager records '{value}' as a tools folder, and Deguffer leaves it "
                    + $"alone: {ConfiguredFolder.NotAFullPath}"));

                return;
            }

            // Resolved once for every recorded folder, and only where the record names one.
            temporary ??= TempRoots.Resolve(environment, machine, volumes).AccountFolders;

            if (ConfiguredFolder.WhyNotOwned(named, environment, machine, volumes, temporary) is { } why)
            {
                withheld = true;
                declinedFolders.Add(named);
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"The ESP-IDF Installation Manager records '{LongPath.Display(named)}' as a tools folder, and "
                    + $"Deguffer leaves it alone: {why}"));

                return;
            }

            Add(named, RecordedReason);
        }

        // One tools folder, examined once however it is named. A folder with no dist in it has nothing
        // to examine, and is passed over without a word, as an absent default is.
        void Add(string folder, string reason)
        {
            if (LongPath.ProbeDirectory(Path.Combine(folder, DownloadFolder)) is PathPresence.Absent)
            {
                return;
            }

            var reached = ReachedFolder.At(folder, volumes);

            if (!folders.Exists(known => known.Reached.IsSameAs(reached)))
            {
                folders.Add((new EspIdfToolsFolder(folder, reason), reached));
            }
        }
    }

    /// <summary>
    /// The <c>idfToolsPath</c> of each installation the record lists, or null where the record could not
    /// be read as one. An installation without one names no folder, and is passed over.
    /// </summary>
    private static IReadOnlyList<string>? RecordedToolsFolders(string record)
    {
        using var document = BoundedJsonFile.Read(record, MaximumRecordBytes);

        if (document is null
            || !document.RootElement.TryGetProperty("idfInstalled", out var installed)
            || installed.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return
        [
            .. installed.EnumerateArray()
                .Where(installation => installation.ValueKind == JsonValueKind.Object)
                .Select(installation => BoundedJsonFile.StringProperty(installation, "idfToolsPath"))
                .OfType<string>()
                .Where(value => value.Trim().Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }
}

/// <summary>One ESP-IDF tools folder, which holds the <c>dist</c> its archives are downloaded into.</summary>
/// <param name="Path">The tools folder. Never a target.</param>
/// <param name="Reason">Why it must survive, written for the user.</param>
public sealed record EspIdfToolsFolder(string Path, string Reason)
{
    /// <summary>The folder its archives are downloaded into. Only the archives in it are removed.</summary>
    public string Downloads => System.IO.Path.Combine(Path, EspIdfLayout.DownloadFolder);
}
