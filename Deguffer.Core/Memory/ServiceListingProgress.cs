namespace Deguffer.Core.Memory;

/// <summary>
/// What one call to <c>EnumServicesStatusEx</c> means for how much of the service list has been read.
///
/// <para>The answer is the one the view states to the reader (§7.2), so what each call means is
/// decided here rather than inside the loop that calls Windows. The loop keeps one rule of its own: a
/// read still going on after as many calls as several thousand services need stops there, read in
/// part.</para>
/// </summary>
internal static class ServiceListingProgress
{
    /// <summary>How much of the list was read, or null where the list goes on and the next call should be made.</summary>
    /// <param name="finished">The call succeeded, so its entries were the last.</param>
    /// <param name="moreData">
    /// The call failed with <c>ERROR_MORE_DATA</c>. Its entries are still valid, and more follow them.
    /// </param>
    /// <param name="parsed">Every entry the call returned could be read inside the buffer.</param>
    /// <param name="returned">How many entries the call returned.</param>
    public static ServiceListing? After(bool finished, bool moreData, bool parsed, int returned)
    {
        if (!(finished || moreData) || !parsed)
        {
            return ServiceListing.ListedInPart;
        }

        if (finished)
        {
            return ServiceListing.Listed;
        }

        // More to come and nothing handed over means one entry larger than the largest buffer the call
        // documents accepting, and calling again would only ask for it again.
        return returned == 0 ? ServiceListing.ListedInPart : null;
    }
}
