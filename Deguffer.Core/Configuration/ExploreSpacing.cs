namespace Deguffer.Core.Configuration;

/// <summary>
/// How much room the treemap leaves round what each folder holds.
///
/// <para>A folder with room for it is drawn as a frame: a band along its top with its name in it,
/// and a gap of this width down its sides and along its bottom. The frame is what shows where one
/// folder ends and the next begins, and a wider one is easier to follow and costs the picture some
/// area. See <c>TreemapLayout</c> for the distortion that cost is.</para>
///
/// <para>The values are ordinal, and the Settings page lists them in this order. They are stored by
/// name, as <see cref="ExploreView"/> is.</para>
/// </summary>
public enum ExploreSpacing
{
    Dense = 0,
    Comfortable = 1,
    Spacious = 2,
}
