namespace Deguffer.Core.InstalledApps;

/// <summary>Which list an entry belongs in (§7.3).</summary>
public enum EntryStanding
{
    /// <summary>The machine proves what the entry describes is gone. The only removable kind.</summary>
    Stale,

    /// <summary>The machine shows the program is there.</summary>
    Installed,

    /// <summary>
    /// Nothing proved the program there or gone. Listed with the installed entries, because short of
    /// proof an entry stays.
    /// </summary>
    Unproven,

    /// <summary>A Windows Installer product installed for another account. Refused both actions.</summary>
    OtherAccount,
}

/// <param name="Reason">What the evidence showed, in the user's words.</param>
public sealed record StandingVerdict(EntryStanding Standing, string Reason)
{
    public bool IsStale => Standing is EntryStanding.Stale;
}
