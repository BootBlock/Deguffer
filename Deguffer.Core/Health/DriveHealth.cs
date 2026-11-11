using System.Security.Cryptography;
using System.Text;
using Deguffer.Core.Health.Parsing;

namespace Deguffer.Core.Health;

/// <summary>
/// A drive's serial number. It identifies a physical device and its owner's machine, so its
/// <see cref="ToString"/> never prints it, and a record that holds one cannot put it in a log.
/// </summary>
public readonly record struct DriveSerial(string Text)
{
    public override string ToString() => "(serial number)";
}

/// <summary>
/// What a drive is known by from one reading to the next: a hash of its model and serial number, so
/// that a drive moved to another port stays the same row and the key says nothing about the drive.
/// A drive with no serial number is known by its disk number instead.
/// </summary>
public readonly record struct DriveKey(string Token)
{
    /// <summary>Enough of the hash to tell a machine's drives apart.</summary>
    private const int HashBytes = 8;

    internal static DriveKey Of(int disk) => new($"disk-{disk}");

    internal static DriveKey Of(string model, DriveSerial serial, int disk) =>
        string.IsNullOrEmpty(serial.Text)
            ? Of(disk)
            : new DriveKey(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{model}\n{serial.Text}")), 0, HashBytes));
}

/// <summary>A drive found on the machine, before it has been read.</summary>
public sealed record DriveDescription(DriveKey Key, int Disk, string Name)
{
    /// <summary>What the drive says about itself, or null where it would not.</summary>
    internal DeviceDescriptor? Descriptor { get; init; }
}

/// <summary>
/// One labelled detail of a drive. <see cref="ToString"/> gives the label alone, because the value
/// can be a serial number.
/// </summary>
public sealed record DriveFact(string Label, string Value)
{
    public override string ToString() => Label;
}

/// <summary>How much a drive's readings worry the page, least first, so the worst is the largest.</summary>
public enum AttributeStanding
{
    Normal,
    Watch,
    Failing,
}

/// <summary>One reading of a drive, in words, with the numbers behind it.</summary>
/// <param name="Key">What the reading is, stable from one read to the next.</param>
/// <param name="Name">The reading's name.</param>
/// <param name="Reading">Its value in words and units.</param>
/// <param name="Meaning">What the value means for the reader.</param>
/// <param name="Standing">Whether the value is a concern.</param>
/// <param name="Raw">The numbers as the drive holds them.</param>
public sealed record HealthAttribute(string Key, string Name, string Reading, string Meaning, AttributeStanding Standing, string Raw);

/// <summary>What the page says overall about one drive.</summary>
public enum DriveVerdict
{
    Unknown,
    Good,
    Watch,
    Failing,
}

/// <summary>One drive's whole reading.</summary>
/// <param name="Summary">One sentence of what the verdict means.</param>
/// <param name="Temperature">The drive's temperature in words, or null where it does not report one.</param>
/// <param name="Findings">One sentence per reading of concern, worst first.</param>
/// <param name="Facts">The drive's details.</param>
/// <param name="Attributes">Its readings, the primary signal first.</param>
/// <param name="MoreWhenElevated">Whether administrator rights would show more of this drive.</param>
public sealed record DriveHealth(
    DriveKey Key,
    string Name,
    DriveVerdict Verdict,
    string Summary,
    string? Temperature,
    IReadOnlyList<string> Findings,
    IReadOnlyList<DriveFact> Facts,
    IReadOnlyList<HealthAttribute> Attributes,
    bool MoreWhenElevated);

/// <summary>What is stored on one drive, as a line of volumes. Reported apart, because a volume can stall where the drive does not.</summary>
/// <param name="RootPaths">Every place each of those volumes is mounted, so a page that lists volumes can find the drive under one by any of them.</param>
public sealed record DriveVolumes(DriveKey Key, string Line, IReadOnlyList<string> RootPaths);
