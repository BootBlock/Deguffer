using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Deguffer.Core.Safety;

/// <summary>
/// What <c>pnputil /enum-drivers /devices /format xml</c> says about the driver store.
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
    /// <summary>The arguments that produce the listing this reads.</summary>
    public const string Arguments = "/enum-drivers /devices /format xml";

    /// <param name="xml">The listing.</param>
    /// <param name="locate">Each package's folder in the driver store, given its published name.</param>
    /// <returns>
    /// The packages, and how many entries were left out because they did not read; or null where the
    /// text is not the listing at all.
    /// </returns>
    public static (IReadOnlyList<DriverPackage> Packages, int Unread)? Parse(string xml, Func<string, string?> locate)
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
            if (Read(entry, locate) is { } package)
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

    private static DriverPackage? Read(XElement entry, Func<string, string?> locate)
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
            InUse: entry.Element("Devices")?.Elements("Device").Any() == true,
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
