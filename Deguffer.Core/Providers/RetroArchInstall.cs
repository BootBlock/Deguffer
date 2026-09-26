using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// One setting in <c>retroarch.cfg</c> that names a folder a RetroArch row looks in.
/// </summary>
/// <param name="Key">The key, which RetroArch compares case-sensitively.</param>
/// <param name="Default">
/// The folder RetroArch uses where no entry sets the key, in RetroArch's own form. Every Windows
/// default is relative to the program's folder.
/// </param>
/// <param name="DefaultWordClears">
/// Whether RetroArch reads the value <c>default</c> as no folder at all. It does for the thumbnails and
/// the shaders, and takes the word literally for the database.
/// </param>
internal sealed record RetroArchFolderSetting(string Key, string Default, bool DefaultWordClears)
{
    public static readonly RetroArchFolderSetting Thumbnails = new("thumbnails_directory", @":\thumbnails", true);

    public static readonly RetroArchFolderSetting Shaders = new("video_shader_dir", @":\shaders", true);

    public static readonly RetroArchFolderSetting Database = new("content_database_path", @":\database\rdb", false);
}

/// <summary>What a folder setting came to.</summary>
/// <param name="Path">The folder, or null where RetroArch uses none or it could not be worked out.</param>
/// <param name="Unresolved">
/// Why the folder could not be worked out, as the end of a sentence, or null where it was, or where
/// RetroArch uses no folder for this.
/// </param>
internal sealed record RetroArchFolder(string? Path, string? Unresolved);

/// <summary>
/// A RetroArch the rows found: the program's folder, and the settings that program reads.
///
/// <para><b>The program's folder is what every default is relative to.</b> RetroArch's Windows defaults
/// all begin <c>:\</c>, which it expands to the folder <c>retroarch.exe</c> is in, and it writes a
/// folder there back in that form when it saves. So the data is beside the program in essentially
/// every install, and a settings file whose program is not known can say where a folder is only where
/// the user gave a full path.</para>
/// </summary>
/// <param name="Program">
/// The folder holding <c>retroarch.exe</c>, or null for settings found in <c>%APPDATA%</c> that no
/// program found was using.
/// </param>
/// <param name="Settings">
/// What RetroArch reads, or null where no settings file exists and every folder is its default.
/// </param>
internal sealed record RetroArchInstall(string? Program, RetroArchSettings? Settings)
{
    /// <summary>
    /// The programs a RetroArch build is. <c>retroarch_angle</c> is the build that draws through
    /// ANGLE, and keeps its data beside it in the same way.
    /// </summary>
    public static readonly IReadOnlyList<string> ProcessNames = ["retroarch", "retroarch_angle"];

    /// <summary>The settings file RetroArch reads first, beside the program.</summary>
    public const string SettingsFileName = "retroarch.cfg";

    /// <summary>
    /// Whether <paramref name="folder"/> holds a RetroArch program. A refusal is kept apart from absence,
    /// because a folder Windows would not describe has not been shown to hold no RetroArch.
    /// </summary>
    public static PathPresence ProgramIn(string folder)
    {
        var refused = false;

        foreach (var name in ProcessNames)
        {
            switch (LongPath.ProbeFile(Path.Combine(folder, name + ".exe")))
            {
                case PathPresence.Present:
                    return PathPresence.Present;

                case PathPresence.Refused:
                    refused = true;
                    break;
            }
        }

        return refused ? PathPresence.Refused : PathPresence.Absent;
    }

    /// <summary>
    /// The folder <paramref name="setting"/> names, expanded as RetroArch's
    /// <c>fill_pathname_expand_special</c> expands it: <c>:</c> at the start is the program's folder,
    /// <c>~</c> is <c>HOME</c>, and in either case the character after it is skipped whatever it is.
    /// The rest is appended as text, as RetroArch appends it, rather than combined as a path: a rest
    /// that is itself rooted names no folder RetroArch could use, where combining would take it alone.
    /// </summary>
    public RetroArchFolder Folder(RetroArchFolderSetting setting, IUserEnvironment environment)
    {
        var value = Settings?[setting.Key] ?? setting.Default;

        if (value.Length == 0 || (setting.DefaultWordClears && value == "default"))
        {
            return new RetroArchFolder(null, null);
        }

        string? expanded;

        if (value[0] == ':')
        {
            if (Program is null)
            {
                return new RetroArchFolder(
                    null,
                    $"'{Settings?.Path}' puts it beside the RetroArch program, and Deguffer does not know which "
                    + "folder that is. Add the folder RetroArch is installed in under Emulator folders in Settings.");
            }

            expanded = Appended(Program, value);
        }
        else if (value[0] == '~' && environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home)
        {
            expanded = Appended(home, value);
        }
        else if (Path.IsPathFullyQualified(value))
        {
            expanded = value;
        }
        else
        {
            return new RetroArchFolder(
                null,
                $"'{Settings?.Path}' gives it relative to the folder RetroArch was started from, which "
                + "Deguffer cannot know.");
        }

        return LongPath.Configured(expanded) is { } folder
            ? new RetroArchFolder(folder, null)
            : new RetroArchFolder(null, $"'{Settings?.Path}' names it as something Windows does not accept as a path.");
    }

    /// <summary><paramref name="folder"/>, a separator, and <paramref name="value"/> from its third character.</summary>
    private static string Appended(string folder, string value) =>
        Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar + (value.Length > 2 ? value[2..] : string.Empty);
}
