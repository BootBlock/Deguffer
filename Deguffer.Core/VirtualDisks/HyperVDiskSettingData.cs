using System.Xml;

namespace Deguffer.Core.VirtualDisks;

/// <summary>
/// The <c>ParentPath</c> of an embedded <c>Msvm_VirtualHardDiskSettingData</c>, the CIM-XML instance
/// <c>GetVirtualHardDiskSettingData</c> gives as its <c>SettingData</c>: an <c>INSTANCE</c> element of that
/// class holding a <c>PROPERTY</c> element for each property, with its value in a <c>VALUE</c> child.
///
/// <para>Microsoft: "If the virtual hard disk does not have a parent, then this property is empty" (the
/// <c>Msvm_VirtualHardDiskSettingData</c> page), so an empty or unset <c>ParentPath</c> is a disk with
/// no parent. A <c>ParentPath</c> that is not there at all, or an instance of another class, is an answer
/// that cannot be read, never a disk with no parent: taking it for one would end the chain at a disk
/// that is built on another, and leave that other unregistered.</para>
/// </summary>
internal static class HyperVDiskSettingData
{
    private const string DiskClass = "Msvm_VirtualHardDiskSettingData";

    private const string ParentProperty = "ParentPath";

    /// <summary>The instance is well under a kilobyte. Anything far larger is not it.</summary>
    private const int MaximumCharacters = 64 * 1024;

    /// <summary>A document type is refused outright, so no entity in Hyper-V's answer is expanded or fetched.</summary>
    private static readonly XmlReaderSettings Reading = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaximumCharacters,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    /// <summary>
    /// Whether <paramref name="settingData"/> says what the disk is built on, with that parent in
    /// <paramref name="parent"/>, or null for a disk built on nothing.
    /// </summary>
    public static bool TryReadParent(string? settingData, out string? parent)
    {
        parent = null;

        if (string.IsNullOrEmpty(settingData) || settingData.Length > MaximumCharacters)
        {
            return false;
        }

        try
        {
            using var reader = XmlReader.Create(new StringReader(settingData), Reading);

            if (reader.MoveToContent() != XmlNodeType.Element
                || reader.LocalName != "INSTANCE"
                || reader.GetAttribute("CLASSNAME") != DiskClass)
            {
                return false;
            }

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element
                    && reader.LocalName == "PROPERTY"
                    && reader.GetAttribute("NAME") == ParentProperty)
                {
                    parent = ValueOf(reader) is { Length: > 0 } path ? path : null;
                    return true;
                }
            }

            return false;
        }
        catch (XmlException)
        {
            // Malformed, a document type, or past the size limit: none of them says what the disk is built on.
            parent = null;
            return false;
        }
    }

    /// <summary>The text of the <c>VALUE</c> child of the <c>PROPERTY</c> the reader is on, or null where it has none.</summary>
    private static string? ValueOf(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            return null;
        }

        var depth = reader.Depth;

        while (reader.Read() && reader.Depth > depth)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "VALUE" && reader.Depth == depth + 1)
            {
                return reader.IsEmptyElement ? string.Empty : reader.ReadElementContentAsString();
            }
        }

        return null;
    }
}
