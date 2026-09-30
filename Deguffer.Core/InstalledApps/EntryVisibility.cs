namespace Deguffer.Core.InstalledApps;

/// <summary>Whether Windows lists an entry, and why not when it does not (§7.3).</summary>
public enum EntryVisibility
{
    /// <summary>Windows lists it.</summary>
    Listed,

    /// <summary>The key would not open, so nothing about it is known.</summary>
    Unreadable,

    /// <summary>It has no <c>DisplayName</c>, which Windows requires to list an entry.</summary>
    NoName,

    /// <summary><c>SystemComponent</c> is 1, which tells Windows not to list it.</summary>
    SystemComponent,

    /// <summary>It names a <c>ParentKeyName</c> or an update <c>ReleaseType</c>: an update of another program.</summary>
    Update,

    /// <summary>A Windows Installer product installed for another account, which Windows lists only for that account.</summary>
    OtherAccount,
}

/// <summary>The listing rules, and the sentence each hidden kind carries.</summary>
public static class EntryListing
{
    private static readonly HashSet<string> UpdateReleaseTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Update", "Hotfix", "Security Update", "Update Rollup",
    };

    /// <summary>
    /// Whether Windows lists <paramref name="record"/> by its own values. Another account's product
    /// is decided by Windows Installer, not by the values, so the standing decides that one.
    /// </summary>
    public static EntryVisibility Of(UninstallRecord record)
    {
        if (!record.IsReadable)
        {
            return EntryVisibility.Unreadable;
        }

        var values = record.Values;

        return values.Text("DisplayName") is null ? EntryVisibility.NoName
            : values.Flag("SystemComponent") ? EntryVisibility.SystemComponent
            : values.Text("ParentKeyName") is not null
              || values.Text("ReleaseType") is { } release && UpdateReleaseTypes.Contains(release) ? EntryVisibility.Update
            : EntryVisibility.Listed;
    }

    /// <summary>Why Windows does not list an entry, or null where it does.</summary>
    public static string? WhyHidden(EntryVisibility visibility) => visibility switch
    {
        EntryVisibility.Listed => null,
        EntryVisibility.Unreadable => "Windows would not let Deguffer read this entry.",
        EntryVisibility.NoName => "Windows does not list this entry because it has no display name.",
        EntryVisibility.SystemComponent => "Windows does not list this entry because it is marked as part of another program.",
        EntryVisibility.Update => "Windows lists this as an update of another program, not as a program.",
        EntryVisibility.OtherAccount => "This program was installed for another account, and Windows lists it only for that account.",
        _ => throw new ArgumentOutOfRangeException(nameof(visibility), visibility, null),
    };
}
