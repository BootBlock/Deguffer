using Deguffer.Core.Providers;

namespace Deguffer.Testing;

/// <summary>
/// Asks a <see cref="ToolRoot"/> about a child by name, as a folder.
///
/// <para>A folder is the kind that tests the name rule. Every root that recognises names offers
/// folders, so a file or a link would be refused for its kind whatever its name was, and an assertion
/// that a name is refused would pass without asking about the name at all.</para>
/// </summary>
public static class ToolRootQuestions
{
    public static bool RecognisesFolder(this ToolRoot root, string name) =>
        root.Recognises(new ToolRootChild(name, ChildKind.Folder));
}
