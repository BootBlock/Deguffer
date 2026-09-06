namespace Deguffer.Core.Providers;

/// <summary>
/// Where this machine writes its File History, as the four folders §5.6 has to tell apart.
///
/// <para>A target drive is shared by construction. Windows lays it out as
/// <c>&lt;root&gt;\FileHistory\&lt;account&gt;\&lt;machine&gt;</c>, so one external disk routinely
/// carries a second person's history beside this one, and one person's history of a laptop beside
/// their history of a desktop. Underneath that sit exactly two folders: <c>Data</c>, which holds the
/// saved versions, and <c>Configuration</c>, which holds the catalogue that makes them restorable.
/// Every one of those neighbours is Tier 4, and none of them is ever a target — Deguffer runs
/// Windows' own command and names these only so it can measure a size and assert what survived.</para>
///
/// <para><b>The layout is not documented by Microsoft.</b> Every source describing it is
/// third-party, which is exactly why §5.2 puts the directory itself out of reach: a path deletion
/// would be acting on a shape nobody has published. Holding it as a computed record rather than as
/// four strings keeps the assumption in one place, so a reader can see the whole of what is being
/// assumed and <see cref="FileHistoryDiscovery"/> can decline when the disk does not match it.</para>
/// </summary>
/// <param name="Root">
/// The target device as the configuration names it — a drive root, a UNC share, or a folder on
/// one. Never touched, and named here only so the folders below can be built from it.
/// </param>
/// <param name="UserName">The account whose history this is. See <see cref="Safety.IUserEnvironment.UserName"/>.</param>
/// <param name="MachineName">The machine whose history this is.</param>
public sealed record FileHistoryTarget(string Root, string UserName, string MachineName)
{
    /// <summary>The folder holding every account's history on this target. Shared, so Tier 4.</summary>
    public string FileHistoryRoot => Path.Combine(Root, "FileHistory");

    /// <summary>This account's history, which still holds a folder per machine it has backed up.</summary>
    public string UserDirectory => Path.Combine(FileHistoryRoot, UserName);

    /// <summary>This account's history of this machine.</summary>
    public string MachineDirectory => Path.Combine(UserDirectory, MachineName);

    /// <summary>
    /// The saved versions themselves — what <c>FhManagew.exe -cleanup</c> trims, and the only path
    /// Deguffer measures.
    /// </summary>
    public string DataDirectory => Path.Combine(MachineDirectory, "Data");

    /// <summary>
    /// The catalogue that makes the versions restorable, sitting beside them. Removing it would
    /// leave the data intact and unreachable, so it is asserted to have survived every run.
    /// </summary>
    public string ConfigurationDirectory => Path.Combine(MachineDirectory, "Configuration");
}
