using Deguffer.Core.Safety;

namespace Deguffer.Core.Exploring.Acting;

/// <summary>
/// Outlook's mail stores, which Explore refuses wherever they are (§9).
///
/// <para>Separate from the rest of <see cref="ExploreActionPolicy"/> because it is a fact about
/// Outlook rather than about Windows or about a tool Deguffer cleans (G1), and because it is the one
/// refusal Explore makes by <em>type</em> rather than by place. Everything else it refuses is at an
/// address. A <c>.pst</c> is wherever somebody saved it — an archive on a data disk, a folder they
/// named themselves, a share — so no table of paths can find one, and an exclusion that only
/// knew the default folders would be absent exactly where the file is kept because it is the only
/// copy.</para>
///
/// <para><b>Both types are refused, for different reasons.</b> An <c>.ost</c> looks like a cache, is
/// routinely the largest file on a business machine, and is widely advised as disposable. Outlook
/// does rebuild most of it from the server, but Microsoft documents a folder inside it that is never
/// copied to the server, and the one sentence permitting its deletion is conditional on an account
/// Deguffer cannot see. A <c>.pst</c> is not a copy of anything: it is where a POP or IMAP account
/// delivers, where an archive goes, and where items moved off a server end up.
/// <c>docs/cache-locations.md</c> records the whole argument.</para>
///
/// <para>A name is matched on its extension and nothing else, so <c>archive.pst.txt</c> and
/// <c>archive.pstx</c> are ordinary. A <em>folder</em> named with one of these extensions is refused
/// as well, because the policy is asked about a path and not about what is on the disk there — which
/// errs in the direction that protects mail, on a name Outlook itself never gives a folder.</para>
/// </summary>
internal static class OutlookDataFiles
{
    private static readonly char[] Separators =
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    /// <summary>
    /// The folder Outlook saves new data files in, inside whichever Documents folder the account has.
    /// Matched by name at any depth rather than resolved, because OneDrive and folder redirection
    /// both move Documents, and a refusal resolved against the default would miss the moved one.
    /// </summary>
    private const string SavedDataFilesFolder = "Outlook Files";

    /// <summary>Why Explore will not remove <paramref name="target"/>, or null where this rule has nothing to say.</summary>
    /// <param name="target">A path already through <see cref="LongPath.Configured(string?)"/>.</param>
    public static ExploreVerdict? Refusal(string target)
    {
        switch (Path.GetExtension(target).ToLowerInvariant())
        {
            case ".ost":
                return ExploreVerdict.Refuse(
                    "This is Outlook's offline copy of a mailbox. Most of it is rebuilt from the server, "
                    + "but Outlook keeps the items it could not synchronise only in this file, and "
                    + "Deguffer cannot tell whether any are in it, so it never removes one. Outlook's "
                    + "'Mail to keep offline' setting is the supported way to make it smaller.");

            case ".pst":
                return ExploreVerdict.Refuse(
                    "This is an Outlook data file: an archive, a POP or IMAP account's mail, or items "
                    + "moved off a mail server. It is often the only copy of that mail, so Deguffer "
                    + "never removes one. Outlook's 'Compact Now', in the data file's own settings, is "
                    + "the supported way to make it smaller.");
        }

        return target.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Contains(SavedDataFilesFolder, StringComparer.OrdinalIgnoreCase)
                ? ExploreVerdict.Refuse(
                    "This is the folder Outlook saves its data files in: archives, and the mail of POP "
                    + "and IMAP accounts. What is in it is often the only copy of that mail, so Deguffer "
                    + "removes nothing here.")
                : null;
    }

    /// <summary>
    /// Outlook's own folder for this account, refused with everything in it.
    ///
    /// <para>§5.2's shape with nothing recognised: the offline copy of each mailbox sits beside the
    /// address books and caches Outlook keeps with it, and on older Outlook the data files as well, so
    /// removing anything here is removing part of the store. The files inside are refused by type
    /// anyway. The region is what refuses the folder that holds them, which would otherwise take them
    /// along.</para>
    /// </summary>
    public static ProtectedRegion Region(IUserEnvironment environment) =>
        ProtectedRegion.Refusing(
            Path.Combine(environment.LocalAppData, "Microsoft", "Outlook"),
            RegionScope.PathAndBelow,
            "This is Outlook's own folder for this account: the offline copy of each mailbox, the "
            + "address books kept with it, and on older Outlook its data files too. Deguffer removes "
            + "nothing here, and Outlook's own settings are what make these files smaller.");
}
