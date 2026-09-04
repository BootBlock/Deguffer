using Deguffer.Core.Exploring;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// Whether a page should offer to relaunch elevated, and what the button that does it says.
///
/// §6.3 runs the app unelevated, so this is the ordinary posture rather than an edge case. The
/// offer answers one question — would administrator rights change anything the user can see? — and
/// what is known to answer it with depends on whether a scan has run yet. Each overload below is
/// one of those states, and both pages that offer the relaunch read them, so the two cannot come to
/// disagree about when it is worth asking for.
///
/// <para>A non-NTFS volume or an unaddressable path takes the walk no matter who is asking, and
/// offering administrator rights for that alone would be a lie.</para>
/// </summary>
public static class ElevationOffer
{
    /// <summary>
    /// Nothing has been scanned yet, so nothing is known beyond which rights this process holds,
    /// and the offer stands on that alone.
    ///
    /// <para>Withholding it until a scan has run makes the elevated scan reachable only through the
    /// slow one it exists to replace: the user must first sit through the walk, on the page that
    /// already tells them a walk is what they are getting. A user who knows they want the file
    /// table, or a step under the Windows directory, is made to pay for that knowledge.</para>
    ///
    /// <para>The button is not a promise of a quicker run. The table answers a location without
    /// walking it, and building it costs one pass over the volume, which on a machine with several
    /// volumes and a modest source tree came to more than it saved. See
    /// <see cref="Deguffer.App.Views.AboutPage"/>'s scan-mode note for what was measured.</para>
    /// </summary>
    public static bool ShouldOffer(bool isElevated) => !isElevated;

    /// <summary>
    /// What a finished Storage preview found. Two unrelated things are improved by elevating, and
    /// both are read here:
    ///
    /// <list type="bullet">
    /// <item><see cref="FallbackReason.NotElevated"/> — a size that had to be walked for rather than
    /// read from the file table. The number is right.</item>
    /// <item><see cref="CleanupPlan.RequiresElevation"/> — a step that cannot be carried out at all,
    /// whatever route measured it. The Windows directory and <c>%PROGRAMDATA%</c> are where this
    /// arises.</item>
    /// </list>
    ///
    /// <para>A plan that fell back for a reason elevation cannot fix may still hold a step that
    /// needs the rights, which is why the two conditions are independent rather than nested.</para>
    /// </summary>
    public static bool ShouldOffer(bool isElevated, IEnumerable<Finding> findings) =>
        !isElevated && findings.Any(f =>
            f.Plan?.Fallback is FallbackReason.NotElevated || f.Plan?.RequiresElevation is true);

    /// <summary>
    /// What a finished Explore scan found. That page draws one volume by one route, so it has a
    /// single fallback reason rather than a plan per provider, and it removes nothing during the
    /// scan — leaving the route as the whole of the question.
    /// </summary>
    /// <param name="fallback">Why <see cref="ExploreScan"/> walked, or
    /// <see cref="FallbackReason.None"/> where it did not.</param>
    public static bool ShouldOffer(bool isElevated, FallbackReason fallback) =>
        !isElevated && fallback is FallbackReason.NotElevated;

    /// <summary>
    /// What the button says, given whether a scan has finished on the page showing it.
    ///
    /// <para>"Rescan" is only true once something has been scanned. Offering to redo work the user
    /// has not done yet reads as a screen that has lost track of what happened on it, and it is the
    /// wording — not the button — that made the elevated scan look like a second step.</para>
    ///
    /// <para>Here rather than in each page's markup, so the two buttons and the sentence that names
    /// one of them cannot come to disagree.</para>
    /// </summary>
    public static string Label(bool hasScanned) =>
        hasScanned ? "Elevate and rescan" : "Elevate and scan";
}
