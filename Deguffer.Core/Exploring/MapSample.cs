namespace Deguffer.Core.Exploring;

/// <summary>
/// An invented tree for a map that shows how a look will draw rather than what is on the disk: the
/// preview beside the appearance choices.
///
/// <para>Nothing in it is read from the machine, so a screenshot of the preview names no folder the
/// reader has. The root is a bare name rather than a path for the same reason.</para>
///
/// <para>Shaped so every choice the preview stands for shows on it: several top-level folders, so a
/// scheme's colours for each branch are side by side, and folders inside folders under more than one
/// of them, so the room spacing leaves round what each folder holds is visible at more than one
/// depth.</para>
/// </summary>
public static class MapSample
{
    private const long KB = 1024;

    private const long MB = 1024 * KB;

    /// <summary>The sample, built once: it never changes.</summary>
    public static ExploreTree Tree { get; } = Build();

    private static ExploreTree Build()
    {
        var builder = new ExploreTreeBuilder("Sample");
        var top = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [
                Folder("Projects"),
                Folder("Photos"),
                Folder("Games"),
                Folder("Music"),
                Folder("Documents"),
                File("archive.zip", 180 * MB),
            ]);

        var projects = builder.AddChildren(
            top,
            [
                Folder("Website"),
                Folder("Engine"),
                File("notes.md", 40 * MB),
            ]);
        builder.AddChildren(projects, [File("bundle.js", 120 * MB), File("index.html", 60 * MB), File("logo.png", 35 * MB)]);
        builder.AddChildren(projects + 1, [File("engine.dll", 210 * MB), File("engine.pdb", 150 * MB), File("shaders.bin", 70 * MB)]);

        var photos = builder.AddChildren(top + 1, [Folder("Holiday"), Folder("Family"), File("cover.jpg", 30 * MB)]);
        builder.AddChildren(photos, [File("beach.jpg", 90 * MB), File("hills.jpg", 75 * MB), File("harbour.jpg", 55 * MB)]);
        builder.AddChildren(photos + 1, [File("garden.jpg", 80 * MB), File("party.jpg", 45 * MB)]);

        var games = builder.AddChildren(top + 2, [Folder("Saves"), File("world.pak", 520 * MB), File("textures.pak", 340 * MB)]);
        builder.AddChildren(games, [File("slot1.sav", 25 * MB), File("slot2.sav", 20 * MB)]);

        builder.AddChildren(
            top + 3,
            [File("album1.flac", 160 * MB), File("album2.flac", 130 * MB), File("album3.flac", 95 * MB), File("single.mp3", 12 * MB)]);

        builder.AddChildren(
            top + 4,
            [
                File("report.pdf", 18 * MB),
                File("budget.xlsx", 9 * MB),
                File("letter.docx", 4 * MB),
                File("slides.pptx", 26 * MB),
                File("scan.pdf", 14 * MB),
            ]);

        return builder.Build(ExploreChildOrder.BySize);
    }

    private static ExploreChild Folder(string name) => new(name, IsDirectory: true, IsLink: false, Size: 0);

    private static ExploreChild File(string name, long size) => new(name, IsDirectory: false, IsLink: false, Size: size);
}
