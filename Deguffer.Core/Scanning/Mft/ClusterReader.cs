namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// Fill <paramref name="destination"/>, a whole number of clusters long, from the volume's clusters
/// starting at <paramref name="firstCluster"/>. False unless every byte was read.
///
/// <para>A delegate as well as <see cref="IMftSource.TryReadClusters"/> because <c>$MFT</c>'s own
/// extents have to be read this way before there is a source to serve a single record.</para>
/// </summary>
internal delegate bool ClusterReader(long firstCluster, Span<byte> destination);
