using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Everything <c>pip cache dir</c> reads to say where pip's cache is, for <see cref="ToolAnswerMemory"/>.
///
/// <para><b>The sources.</b> pip's configuration is read, in order, from the site-wide
/// <c>%ProgramData%\pip\pip.ini</c>, the legacy <c>%USERPROFILE%\pip\pip.ini</c>, the roaming
/// <c>%APPDATA%\pip\pip.ini</c>, the interpreter's <c>&lt;sys.prefix&gt;\pip.ini</c>, and the file
/// <c>PIP_CONFIG_FILE</c> names (https://pip.pypa.io/en/stable/topics/configuration/, and
/// <c>get_configuration_files</c> in pip's <c>configuration.py</c>). pip does not read
/// <c>%LOCALAPPDATA%\pip\pip.ini</c>. The variables need no entry: the fingerprint holds the whole
/// block the tool is given.</para>
///
/// <para><b>The code.</b> A different pip can answer differently, so the launcher, the interpreter it
/// names, the prefix's <c>site-packages</c> (an upgrade renames pip's <c>dist-info</c> folder there)
/// and the user site under <c>%APPDATA%\Python</c> (a <c>pip install --user pip</c> there takes
/// precedence) are read too.</para>
/// </summary>
internal static class PipLocationInputs
{
    /// <summary>
    /// Where the launcher names no interpreter (a <c>pip.bat</c>, or a file Windows refused), the
    /// prefix is unknown, and so are two of pip's sources. A relative path is never read and always
    /// reads as refused (<see cref="InputStamp.Of(string)"/>), so naming one makes the fingerprint
    /// incomplete and the answer is never kept, which is what an unknown input must do.
    /// </summary>
    internal const string UnknownPrefix = "sys.prefix of a launcher that names no interpreter";

    /// <summary>
    /// The user site's layout is <c>Python\PythonXY\site-packages\&lt;distribution&gt;</c>, so three
    /// levels reach the folder an install or an upgrade adds or renames.
    /// </summary>
    private const int UserSiteDepth = 3;

    /// <summary>What <paramref name="pip"/> reads to report its cache directory.</summary>
    /// <param name="pip">The executable asked, as <see cref="PipCommand.Find"/> found it.</param>
    /// <param name="environment">The profile and the variables the tool is started with.</param>
    /// <param name="system">Where the site-wide configuration is.</param>
    public static ToolInputs Of(string pip, IUserEnvironment environment, ISystemDirectories system)
    {
        var files = new List<string>
        {
            pip,
            Path.Combine(system.ProgramData, "pip", "pip.ini"),
            Path.Combine(environment.UserProfile, "pip", "pip.ini"),
            Path.Combine(environment.RoamingAppData, "pip", "pip.ini"),
        };

        var folders = new List<ListedFolder> { new(Path.Combine(environment.RoamingAppData, "Python"), UserSiteDepth) };

        if (environment.ToolEnvironment.TryGetValue("PIP_CONFIG_FILE", out var named)
            && named.Length > 0
            && ToolInputs.Opened(named, environment) is { } configFile)
        {
            files.Add(configFile);
        }

        if (DistlibLauncher.InterpreterOf(pip) is { } interpreter && Path.GetDirectoryName(interpreter) is { } folder)
        {
            // Python takes a pyvenv.cfg beside its folder's parent as a virtual environment rooted
            // there, so creating or removing that file moves the prefix itself. A marker Windows
            // refuses to describe leaves the prefix unknown; it is declared below, and its refused
            // stamp keeps the fingerprint incomplete, so whichever prefix is read here is never kept.
            var venvMarker = Path.Combine(Path.GetDirectoryName(folder) ?? folder, "pyvenv.cfg");
            var prefix = LongPath.ProbeFile(venvMarker) is PathPresence.Present ? Path.GetDirectoryName(folder) ?? folder : folder;

            files.AddRange(
            [
                interpreter,
                venvMarker,
                Path.Combine(prefix, "pyvenv.cfg"),
                Path.Combine(prefix, "pip.ini"),
            ]);
            folders.Add(new ListedFolder(Path.Combine(prefix, "Lib", "site-packages")));
        }
        else
        {
            files.Add(UnknownPrefix);
        }

        return new ToolInputs { Files = files, Folders = folders };
    }
}
