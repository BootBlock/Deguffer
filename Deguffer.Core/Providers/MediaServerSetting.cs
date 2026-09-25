using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>What a media server's settings file said about one folder.</summary>
public enum MediaServerSettingReading
{
    /// <summary>The file is not there, or it names no folder. The server uses its default.</summary>
    NotSet,

    /// <summary>The file names a folder, and <see cref="MediaServerSetting.Folder"/> is it.</summary>
    Set,

    /// <summary>
    /// The file could not be read, or it names something that is not a full path. Nothing
    /// established where the server puts that folder.
    /// </summary>
    Unknown,
}

/// <summary>
/// One folder a media server's XML settings file names, read on the terms the providers need.
///
/// <para><b>Three answers, because two of them lead to opposite plans.</b> "The file names no
/// folder" means the server uses its default, which a provider may offer. "The file could not be
/// read" means the server may use a folder nobody here can name, which the provider owes the user a
/// sentence about. Folding the second into the first would report a configured folder as never
/// having existed.</para>
///
/// <para>Shared by Jellyfin and Emby, which descend from one code base and keep the same
/// <c>config\encoding.xml</c> with the same element in it.</para>
/// </summary>
/// <param name="Reading">What the file said.</param>
/// <param name="Folder">The folder, fully qualified, where <paramref name="Reading"/> is <see cref="MediaServerSettingReading.Set"/>.</param>
/// <param name="File">The settings file, for the sentence a provider owes about an unknown answer.</param>
public sealed record MediaServerSetting(MediaServerSettingReading Reading, string? Folder, string File)
{
    /// <summary>
    /// The value of <paramref name="element"/>, a child of the document's root, in
    /// <paramref name="file"/>. An empty element is no setting: both servers write one for every
    /// option the user has left alone.
    /// </summary>
    public static MediaServerSetting Read(string file, string element)
    {
        if (LongPath.ProbeFile(file) is PathPresence.Absent)
        {
            return new MediaServerSetting(MediaServerSettingReading.NotSet, null, file);
        }

        if (XmlFile.TryLoad(file)?.Root is not { } root)
        {
            return new MediaServerSetting(MediaServerSettingReading.Unknown, null, file);
        }

        var value = root.Element(element)?.Value.Trim();

        if (string.IsNullOrEmpty(value))
        {
            return new MediaServerSetting(MediaServerSettingReading.NotSet, null, file);
        }

        // A relative value would resolve against Deguffer's own working directory, which is a folder
        // nobody pointed the server at. See LongPath.Configured.
        return LongPath.Configured(value) is { } folder
            ? new MediaServerSetting(MediaServerSettingReading.Set, folder, file)
            : new MediaServerSetting(MediaServerSettingReading.Unknown, null, file);
    }
}
