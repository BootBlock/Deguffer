namespace Deguffer.Core.InstalledApps;

/// <summary>What an entry's <c>InstallLocation</c> lets Deguffer check (§7.3).</summary>
public enum InstallLocationKind
{
    /// <summary>The entry names no install location, which leaves nothing to find standing.</summary>
    NotSet,

    /// <summary>A full path Deguffer can ask Windows about.</summary>
    Path,

    /// <summary>
    /// A value that is set but names nothing Deguffer can check: a relative path, a drive with no
    /// folder, or a value that is not text. It may name something still standing, so it proves nothing.
    /// </summary>
    Uncheckable,
}

/// <param name="Text">
/// The path for <see cref="InstallLocationKind.Path"/>, what the value says for an
/// <see cref="InstallLocationKind.Uncheckable"/> string, and empty otherwise.
/// </param>
public readonly record struct InstallLocation(InstallLocationKind Kind, string Text)
{
    /// <summary>
    /// Read <paramref name="values"/>. Some installers write the value quoted, or with a trailing
    /// separator, and both still name a path.
    /// </summary>
    public static InstallLocation Of(UninstallValues values)
    {
        const string Name = "InstallLocation";

        if (!values.IsSet(Name))
        {
            return new(InstallLocationKind.NotSet, string.Empty);
        }

        if (values.Text(Name) is not { } text)
        {
            return new(InstallLocationKind.Uncheckable, string.Empty);
        }

        var location = Path.TrimEndingDirectorySeparator(text.Trim('"').Trim());

        return Path.IsPathFullyQualified(location)
            ? new(InstallLocationKind.Path, location)
            : new(InstallLocationKind.Uncheckable, text);
    }
}
