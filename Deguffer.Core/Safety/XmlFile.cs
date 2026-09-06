using System.Xml;
using System.Xml.Linq;

namespace Deguffer.Core.Safety;

/// <summary>
/// Reading an XML file a tool wrote, on the terms the rest of Core reads files on.
///
/// <para><b>It exists for one line that is easy to write wrongly.</b>
/// <see cref="XDocument.Load(string)"/> treats its argument as a URI, and §6.3's extended-length
/// prefix is not one — <c>\\?\C:\…</c> throws before a byte is read, and a volume-GUID path throws
/// differently. So the file has to be opened as a stream, and every caller that forgets pays for it
/// at run time on exactly the machines whose paths are long. Two providers read XML written by a
/// tool, and both were carrying the same six lines and the same comment explaining them.</para>
///
/// <para><b>Null is the answer to every failure, and that is the contract.</b> A configuration file
/// a tool was part-way through writing, one somebody replaced, and one this account may not read are
/// all the same thing to a caller: nothing was learned, so nothing is offered. §5.3 makes the
/// refusal ordinary rather than an error.</para>
/// </summary>
public static class XmlFile
{
    /// <summary>The document at <paramref name="path"/>, or null where it could not be read.</summary>
    public static XDocument? TryLoad(string path)
    {
        try
        {
            // FileShare.ReadWrite because a tool may hold its own configuration open while Deguffer
            // looks at it, and a refusal there would report the file as unreadable.
            using var stream = new FileStream(
                LongPath.Extended(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // XDocument prohibits DTD processing by default, so a file somebody had replaced cannot
            // pull in an external entity.
            return XDocument.Load(stream);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
