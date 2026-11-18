namespace Deguffer.Core.Providers;

/// <summary>What a declared location is, and how much of it goes.</summary>
public enum DeclaredLocationKind
{
    Directory,
    File,

    /// <summary>
    /// A directory whose contents are removed and which itself stays.
    ///
    /// <para>Declared rather than derived, because it is a fact about the location: <c>%TEMP%</c>
    /// and <c>C:\Windows\Temp</c> are folders the rest of the machine expects to find, and Windows
    /// puts back neither. Every other location here is a directory nothing will miss.</para>
    ///
    /// <para>Such a location is protected as well as targeted, so §5.6 asserts the folder is still
    /// there afterwards. That is the assertion worth having: the ordinary removal takes a directory
    /// with its contents, so a bound that failed to hold would look exactly like an ordinary
    /// deletion and leave nothing to notice.</para>
    /// </summary>
    DirectoryContents,
}

/// <summary>How a declared location is dated, for §7's age column and for a provider that decides by it.</summary>
public enum DeclaredLocationAge
{
    /// <summary>
    /// The newest of the location and its immediate children, or a file's own last-write time.
    ///
    /// <para>Right wherever those children are the things being written: a dump folder, a log
    /// folder, a directory of downloaded archives.</para>
    /// </summary>
    OneLevelDown,

    /// <summary>
    /// The newer of the directory's own creation and last-write times, and nothing inside it.
    ///
    /// <para>For a folder made whole in one step whose contents a person may then rearrange.
    /// <c>Windows.old</c> is created by the upgrade, and the age that decides whether the upgrade can
    /// still be undone is the upgrade's. Moving a file out of the old profile inside it writes the
    /// folder the file left, and dated one level down that would restart the uninstall window that
    /// Windows itself counts from the upgrade. Adding or removing an entry at its top still moves the
    /// folder's own time, which is the direction that holds back.</para>
    /// </summary>
    Itself,

    /// <summary>
    /// No age at all, and §7's column is blank rather than carrying a date nobody should act on.
    ///
    /// <para>For a location that nests before it reaches its content. A Maven local repository is
    /// keyed by group and then artifact and then version, so its top level moves only when a whole
    /// new group first appears, and a repository somebody builds against every day would report as
    /// years old, which is precisely backwards for the one thing an age is read for. Walking deeper
    /// is not the fix: the correct age would cost a full tree walk at plan time, and
    /// <see cref="DeletionTarget.LastWritten"/> already says that one timestamp spanning everything a
    /// tool ever cached is a number with nothing to mean.</para>
    /// </summary>
    Unreported,
}

/// <summary>
/// One path a provider names outright, relative to a <see cref="DeclaredRoot"/>.
///
/// <para>This is §5.2 taken further than a <see cref="Safety.DisposableChildSet"/> can take it.
/// A child set answers "of the children I just enumerated, which may I delete?", which is the right
/// question when the children are discovered. Under <c>C:\Windows</c> there is no such question:
/// the directory must never be enumerated at all, because listing it is the first step towards
/// classifying something in it, and nothing in the operating system's own directory should ever be
/// classified by a rule. So a provider names the exact paths instead, and the unrecognised case
/// cannot arise — there is no enumeration through which an unnamed sibling could be reached.</para>
///
/// <para>It follows that a location carries no tier of its own. A child set has to classify, so its
/// entries can disagree with the provider's tier and a test has to hold them to it; here the
/// provider's tier is the only tier there is.</para>
/// </summary>
/// <param name="RelativePath">
/// Where it sits below the root, which may be several segments deep — <c>Logs\CBS</c>,
/// <c>System32\LogFiles\WMI\RtBackup</c>. Every directory between the root and the target is
/// checked and protected rather than assumed, so depth costs nothing in checkability.
/// </param>
/// <param name="Reason">Why it is disposable, written for the user.</param>
/// <param name="Kind">Directory or file. <c>MEMORY.DMP</c> is the reason the second exists.</param>
/// <param name="Age">How §7's age is read for this location. See <see cref="DeclaredLocationAge"/>.</param>
public sealed record DeclaredLocation(
    string RelativePath,
    string Reason,
    DeclaredLocationKind Kind = DeclaredLocationKind.Directory,
    DeclaredLocationAge Age = DeclaredLocationAge.OneLevelDown);

/// <summary>
/// A directory a provider reaches into by name, and the exact paths under it that it may remove.
///
/// The same table shape <see cref="ShaderCacheRoot"/> uses, for the same reason: the tier, the
/// consequence and the reasoning belong to the provider, and what differs between roots is only
/// which directory and which paths — which is data.
/// </summary>
/// <param name="Path">The root itself. Never enumerated, and never a target.</param>
/// <param name="Reason">Why the root must survive, for §5.6's report.</param>
/// <param name="RequiresElevation">
/// Whether removing anything below this root needs administrator rights. A property of where the
/// directory is rather than of any one entry, which is why it sits here and not on a location.
/// </param>
/// <param name="Locations">What may be removed. Nothing else under the root is ever reached.</param>
/// <param name="ProtectedNames">
/// Paths under the root that §5.6 must assert survived, as relative path and reason.
///
/// This is where §9's exclusions are written down. <c>WinSxS</c> and <c>Windows\Installer</c> are
/// never targets, and stating that as an omission proves nothing — naming them here is what makes a
/// run produce evidence that a rule reaching into <c>C:\Windows</c> did not reach them.
/// </param>
public sealed record DeclaredRoot(
    string Path,
    string Reason,
    bool RequiresElevation,
    IReadOnlyList<DeclaredLocation> Locations,
    IReadOnlyList<(string RelativePath, string Reason)> ProtectedNames);
