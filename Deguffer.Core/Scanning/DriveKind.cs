using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Scanning;

/// <summary>One drive present, and the kind of storage it is on.</summary>
/// <param name="Drive">The drive's letter and colon, such as <c>C:</c>.</param>
public sealed record DriveKind(string Drive, StorageMedia Media);
