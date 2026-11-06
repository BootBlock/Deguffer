using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// One location measured from the table under several guards in one pass. See
/// <see cref="MftVolumeIndex.TryMeasure(IReadOnlyList{string}, IReadOnlyList{MinimumAge}, bool, IReadOnlySet{uint})"/>.
///
/// <para>A guard changes only which entries a removal keeps, so the stores are one list for every
/// reading: a store stays whatever its age.</para>
/// </summary>
/// <param name="Readings">One reading for each guard, in the order the guards were given.</param>
/// <param name="MailStores">
/// The Outlook mail stores the location holds, each as components below the volume root, and none
/// of them in any reading. See <see cref="MailStore"/>.
/// </param>
public sealed record MftMeasurement(IReadOnlyList<MftReading> Readings, IReadOnlyList<IReadOnlyList<string>> MailStores);
