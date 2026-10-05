namespace Deguffer.App.ViewModels;

/// <summary>One drive's line in the Settings page's Scanning section.</summary>
/// <param name="Drive">The drive's letter and colon, which is what the line is matched by.</param>
public sealed record DriveLine(string Drive, string Text);
