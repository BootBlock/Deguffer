using System.Text;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// How recognising an <c>obj</c> reads its restore manifest: the shapes NuGet writes, the shapes a
/// hand edit or a crash leaves, and what reading a real-sized one costs.
///
/// The manifest is the first of the three sources that must agree before an <c>obj</c> is deleted,
/// so a reading that accepts a file it should refuse is a §5.2 failure, not a parsing nicety.
/// </summary>
public sealed class DotNetIntermediateManifestTests : IDisposable
{
    /// <summary>The manifest's own record of the project, JSON-escaped. Only its filename is used.</summary>
    private const string ExamplePath = """
        "C:\\Users\\testuser\\src\\Example\\Example.csproj"
        """;

    private const string OtherPath = """
        "C:\\Users\\testuser\\src\\Other\\Other.csproj"
        """;

    private const string Restore = $$"""
        "restore": { "projectPath": {{ExamplePath}}, "projectName": "Example" }
        """;

    /// <summary>The JSON escape for a low surrogate with no high surrogate before it.</summary>
    private const string LoneSurrogate = @"\" + "uDC00";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string? Recognise(string manifest) =>
        DotNetIntermediateSignature.TryRecognise(
            ProjectFixture.CreateProject(_temp.CreateDirectory("Example"), "Example", rawManifest: manifest))
            ?.ProjectName;

    private string? Recognise(byte[] manifest)
    {
        var obj = ProjectFixture.CreateProject(_temp.CreateDirectory("Example"), "Example");
        File.WriteAllBytes(Path.Combine(obj, "project.assets.json"), manifest);

        return DotNetIntermediateSignature.TryRecognise(obj)?.ProjectName;
    }

    /// <summary>
    /// The order NuGet writes: about a megabyte of <c>targets</c> and <c>libraries</c>, then
    /// <c>project</c> last. The package hash is longer than the reader's window, so the window has to
    /// grow to hold one token as well as refill across thousands of boundaries.
    /// </summary>
    [Fact]
    public void Recognises_a_real_sized_manifest_whose_project_comes_last()
    {
        Assert.Equal("Example.csproj", Recognise(RealSized($$"""{ {{Restore}} }""")));
    }

    [Fact]
    public void Recognises_a_manifest_whose_project_comes_before_its_targets()
    {
        Assert.Equal(
            "Example.csproj",
            Recognise($$"""{ "version": 3, "project": { {{Restore}} }, "targets": { "net10.0": {} } }"""));
    }

    /// <summary>
    /// A manifest cut off once both values have been seen. Everything this rule asks for is present,
    /// and the file is still not one NuGet finished writing, so it is refused.
    /// </summary>
    [Fact]
    public void Refuses_a_real_sized_manifest_truncated_just_after_its_project_path()
    {
        var whole = RealSized($$"""{ "restore": { "projectPath": {{ExamplePath}} } }""");

        Assert.Null(Recognise(whole[..(whole.LastIndexOf(ExamplePath, StringComparison.Ordinal) + ExamplePath.Length)]));
    }

    /// <summary>
    /// A field of the identity defined twice. In each case the last definition names this project, so
    /// a last-wins reading would accept it. Which one the manifest means cannot be known, so it is
    /// refused.
    /// </summary>
    [Theory]
    [InlineData($$"""{ "version": 99, "version": 3, "project": { {{Restore}} } }""")]
    [InlineData($$"""{ "version": 3, "project": { "restore": { "projectPath": {{OtherPath}} } }, "project": { {{Restore}} } }""")]
    [InlineData($$"""{ "version": 3, "project": { "restore": { "projectPath": {{OtherPath}} }, {{Restore}} } }""")]
    [InlineData($$"""{ "version": 3, "project": { "restore": { "projectPath": {{OtherPath}}, "projectPath": {{ExamplePath}} } } }""")]
    [InlineData($$"""{ "version": 3, "project": { "restore": { "projectPath": {{ExamplePath}}, "projectPath": {{ExamplePath}} } } }""")]
    public void Refuses_a_manifest_that_defines_a_field_of_its_identity_twice(string manifest)
    {
        Assert.Null(Recognise(manifest));
    }

    /// <summary>
    /// Duplicates the identity does not depend on, and properties named like its fields in other
    /// objects. Neither is evidence about which project the manifest is for.
    /// </summary>
    [Theory]
    [InlineData($$"""{ "version": 3, "targets": { "a": 1, "a": 2 }, "project": { "restore": { "projectPath": {{ExamplePath}}, "projectName": "A", "projectName": "B" } } }""")]
    [InlineData($$"""{ "version": 3, "targets": { "restore": { "projectPath": {{OtherPath}} } }, "project": { {{Restore}} } }""")]
    [InlineData($$"""{ "version": 3, "project": { "frameworks": { "projectPath": {{OtherPath}}, "restore": { "projectPath": {{OtherPath}} } }, {{Restore}} } }""")]
    [InlineData($$"""{ "version": 3, "project": { {{Restore}}, "version": 99 }, "projectPath": {{OtherPath}} }""")]
    [InlineData($$"""{ "version": 3, "project": { {{Restore}}, "frameworks": { "projectPath": {{OtherPath}} } } }""")]
    [InlineData($$"""{ "version": 3, "project": { {{Restore}} }, "targets": { "restore": { "projectPath": {{OtherPath}} } } }""")]
    public void Ignores_duplicates_and_namesakes_outside_the_identity(string manifest)
    {
        Assert.Equal("Example.csproj", Recognise(manifest));
    }

    /// <summary>A <c>projectPath</c> in the wrong object is not the project's, however apt its value.</summary>
    [Theory]
    [InlineData($$"""{ "version": 3, "targets": { {{Restore}} } }""")]
    [InlineData($$"""{ "version": 3, "project": { "frameworks": { {{Restore}} } } }""")]
    [InlineData($$"""{ "version": 3, "project": { "projectPath": {{ExamplePath}} } }""")]
    [InlineData($$"""{ "version": 3, {{Restore}} }""")]
    [InlineData($$"""{ "version": 3, "project": [ { {{Restore}} } ] }""")]
    [InlineData($$"""{ "version": 3, "project": { "restore": [ { "projectPath": {{ExamplePath}} } ] } }""")]
    public void Refuses_a_project_path_outside_project_restore(string manifest)
    {
        Assert.Null(Recognise(manifest));
    }

    /// <summary>
    /// A project path that is not valid UTF-8. Decoding it throws
    /// <see cref="InvalidOperationException"/>, which must make the directory Tier 4 rather than
    /// escape recognition.
    /// </summary>
    [Fact]
    public void Refuses_a_project_path_that_is_not_valid_utf8_without_throwing()
    {
        const string text = $$"""{ "version": 3, "project": { {{Restore}} } }""";
        var manifest = Encoding.ASCII.GetBytes(text);

        // The directory's first letter, so the filename the rule takes is still a valid project name.
        manifest[text.IndexOf(@"\\Example\\", StringComparison.Ordinal) + 2] = 0xFF;

        Assert.Null(Recognise(manifest));
    }

    /// <summary>
    /// An escape for half a surrogate pair. The tokenizer accepts its form, and comparing a property
    /// name or reading a string decodes it, which throws <see cref="InvalidOperationException"/>. The
    /// name must be long enough for the comparison to decode it rather than reject it by length.
    /// </summary>
    [Theory]
    [InlineData($$"""{ "{{LoneSurrogate}}x": 1, "version": 3, "project": { {{Restore}} } }""")]
    [InlineData($$"""{ "version": 3, "project": { "restore": { "{{LoneSurrogate}}xxxxxxxxxxx": 1, "projectPath": {{ExamplePath}} } } }""")]
    [InlineData($$"""{ "version": 3, "project": { "restore": { "projectPath": "C:\\src\\{{LoneSurrogate}}\\Example.csproj" } } }""")]
    public void Refuses_text_that_cannot_be_decoded_without_throwing(string manifest)
    {
        Assert.Null(Recognise(manifest));
    }

    /// <summary>Forms valid JSON allows that a hand edit or another writer may produce.</summary>
    [Fact]
    public void Recognises_a_manifest_that_starts_with_a_byte_order_mark()
    {
        Assert.Equal(
            "Example.csproj",
            Recognise([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes($$"""{ "version": 3, "project": { {{Restore}} } }""")]));
    }

    [Fact]
    public void Recognises_property_names_written_with_escapes()
    {
        Assert.Equal(
            "Example.csproj",
            Recognise($$"""{ "vers\u0069on": 3, "project": { "restore": { "project\u0050ath": {{ExamplePath}} } } }"""));
    }

    /// <summary>
    /// A value after the document ends. The identity is complete and valid, and the file is still
    /// not one JSON document.
    /// </summary>
    [Theory]
    [InlineData($$"""{ "version": 3, "project": { {{Restore}} } } {}""")]
    [InlineData($$"""{ "version": 3, "project": { {{Restore}} } } // written by hand""")]
    public void Refuses_content_after_the_document(string manifest)
    {
        Assert.Null(Recognise(manifest));
    }

    /// <summary>
    /// Reading a megabyte manifest allocates a small fraction of it. A <see cref="System.Text.Json.JsonDocument"/>
    /// of the same file allocates three to four megabytes: the file itself and a row for each of its
    /// tokens.
    ///
    /// The document rents both from the shared array pool, so once a thread has read one manifest
    /// the next allocates almost nothing, and what it costs is the memory the pool goes on holding.
    /// A new thread starts with no arrays of its own, which is what lets the first read show the
    /// working set. The reader rents one window from the same pool, so the bound here covers it.
    /// </summary>
    [Fact]
    public void Reading_a_real_sized_manifest_allocates_a_fraction_of_its_size()
    {
        var manifest = RealSized($$"""{ {{Restore}} }""");
        var obj = ProjectFixture.CreateProject(_temp.CreateDirectory("Example"), "Example", rawManifest: manifest);
        var allocated = 0L;
        RecognisedProject? recognised = null;

        var thread = new Thread(() =>
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            recognised = DotNetIntermediateSignature.TryRecognise(obj);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        });

        thread.Start();
        thread.Join();

        Assert.NotNull(recognised);
        Assert.True(manifest.Length > 1_000_000, $"The manifest was only {manifest.Length} characters.");
        Assert.True(allocated < 256 * 1024, $"Reading a {manifest.Length}-byte manifest allocated {allocated} bytes.");
    }

    /// <summary>
    /// A manifest of the size and order NuGet writes, ending with <paramref name="project"/>. One
    /// package hash is 48 KB, three times the reader's window.
    /// </summary>
    private static string RealSized(string project)
    {
        var json = new StringBuilder("""{ "version": 3, "targets": { "net10.0": {""");

        for (var i = 0; json.Length < 600_000; i++)
        {
            json.Append(i == 0 ? "" : ",")
                .Append($$"""
                    "Package{{i}}/1.0.{{i}}": { "type": "package", "dependencies": { "Dependency{{i}}": "[2.0.0, )" },
                      "compile": { "lib/net10.0/Package{{i}}.dll": { "related": ".xml" } },
                      "runtime": { "lib/net10.0/Package{{i}}.dll": {} } }
                    """);
        }

        json.Append("""} }, "libraries": {""");

        for (var i = 0; json.Length < 1_050_000; i++)
        {
            var sha512 = i == 0 ? new string('A', 48 * 1024) : "sha512-" + new string('b', 86);
            json.Append(i == 0 ? "" : ",")
                .Append($$"""
                    "Package{{i}}/1.0.{{i}}": { "sha512": "{{sha512}}", "type": "package", "path": "package{{i}}/1.0.{{i}}",
                      "files": [ "lib/net10.0/Package{{i}}.dll", "lib/net10.0/Package{{i}}.xml" ] }
                    """);
        }

        return json.Append($$"""}, "packageFolders": { "C:\\Users\\testuser\\.nuget\\packages\\": {} }, "project": {{project}} }""")
            .ToString();
    }
}
