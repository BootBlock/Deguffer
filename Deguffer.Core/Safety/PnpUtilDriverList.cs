using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Deguffer.Core.Safety;

/// <summary>
/// What <c>pnputil /enum-drivers /format xml</c> says about the driver store.
///
/// <para><b>The XML, never the text.</b> The text form's labels are translated, so a parser keyed on
/// "Published Name:" reads nothing on a German Windows. The XML's element names are not, and its
/// <c>DriverVersion</c> is written month first whatever the machine's own date format is.</para>
///
/// <para><b>An entry that does not read is left out, never guessed at.</b> Leaving one out can only
/// make a package look like the newest of its kind when it is not, which keeps it. It can never make
/// a package look older than one it is not older than.</para>
/// </summary>
public static class PnpUtilDriverList
{
    /// <summary>
    /// The listing without each package's devices, read by <see cref="Parse"/>. Naming the devices is
    /// almost all of pnputil's cost, and <see cref="DeviceDrivers"/> answers which packages are in use.
    /// </summary>
    public const string Arguments = "/enum-drivers /format xml";

    /// <summary>
    /// The listing with each package's devices, read by <see cref="ParseWithDevices"/>, for where the
    /// devices' own properties cannot say which packages are in use.
    /// </summary>
    public const string ArgumentsWithDevices = "/enum-drivers /devices /format xml";

    /// <param name="xml">The listing <see cref="Arguments"/> produce.</param>
    /// <param name="locate">Each package's folder in the driver store, given its published name.</param>
    /// <param name="inUse">Whether some device is installed with a package, given its published name.</param>
    /// <returns>
    /// The packages, and how many entries were left out because they did not read; or null where the
    /// text is not the listing at all.
    /// </returns>
    public static (IReadOnlyList<DriverPackage> Packages, int Unread)? Parse(
        string xml,
        Func<string, string?> locate,
        Func<string, bool> inUse)
    {
        ArgumentNullException.ThrowIfNull(inUse);

        return Read(xml, locate, (_, published) => inUse(published));
    }

    /// <summary>As <see cref="Parse"/>, with each package in use where the listing names a device for it.</summary>
    /// <param name="xml">The listing <see cref="ArgumentsWithDevices"/> produce.</param>
    /// <param name="locate">Each package's folder in the driver store, given its published name.</param>
    public static (IReadOnlyList<DriverPackage> Packages, int Unread)? ParseWithDevices(string xml, Func<string, string?> locate) =>
        Read(xml, locate, (entry, _) => entry.Element("Devices")?.Elements("Device").Any() == true);

    private static (IReadOnlyList<DriverPackage> Packages, int Unread)? Read(
        string xml,
        Func<string, string?> locate,
        Func<XElement, string, bool> inUse)
    {
        ArgumentNullException.ThrowIfNull(locate);

        XDocument document;

        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException)
        {
            // pnputil too old to know /format prints its usage instead, which is not XML.
            return null;
        }

        if (document.Root?.Name.LocalName != "PnpUtil")
        {
            return null;
        }

        var packages = new List<DriverPackage>();
        var unread = 0;

        foreach (var entry in document.Root.Elements("Driver"))
        {
            if (Package(entry, locate, inUse) is { } package)
            {
                packages.Add(package);
            }
            else
            {
                unread++;
            }
        }

        return (packages, unread);
    }

    private static DriverPackage? Package(XElement entry, Func<string, string?> locate, Func<XElement, string, bool> inUse)
    {
        var published = (string?)entry.Attribute("DriverName");
        var original = Text(entry, "OriginalName");
        var provider = Text(entry, "ProviderName");
        var classGuid = Text(entry, "ClassGuid");

        if (published is not { Length: > 0 } || original is null || provider is null || classGuid is null
            || Stamp(Text(entry, "DriverVersion")) is not var (date, version))
        {
            return null;
        }

        return new DriverPackage(
            published,
            original,
            provider,
            classGuid,
            Text(entry, "ExtensionId"),
            date,
            version,
            InUse: inUse(entry, published),
            Folder: locate(published));
    }

    private static string? Text(XElement entry, string name) =>
        entry.Element(name)?.Value.Trim() is { Length: > 0 } value ? value : null;

    /// <summary><c>DriverVer</c> as the listing writes it: <c>07/22/2022 3.0.1511.0</c>.</summary>
    private static (DateOnly Date, Version Version)? Stamp(string? text)
    {
        var parts = text?.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return parts is [var date, var version]
            && DateOnly.TryParseExact(date, "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            && Version.TryParse(version, out var parsed)
                ? (day, parsed)
                : null;
    }
}
