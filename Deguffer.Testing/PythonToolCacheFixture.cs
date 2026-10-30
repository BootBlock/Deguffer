using System.Text;
using Deguffer.Core.Safety;

namespace Deguffer.Testing;

/// <summary>The Python tools whose cache folders carry a cache directory tag.</summary>
public enum PythonToolCache
{
    Pytest,
    Mypy,
    Ruff,
    Tox,
    Nox,
}

/// <summary>
/// Builds a project holding one Python tool's tagged cache folder, on disk, with the files inside
/// exactly as that tool writes them on Windows.
///
/// <para>Each text is taken from the tool's own source, and so are its line endings: pytest and ruff
/// write bytes, so theirs stay <c>\n</c>, while mypy, tox and nox write through Python's text mode,
/// which turns each <c>\n</c> into <c>\r\n</c> on Windows. The sources are cited on each provider.
/// Every piece is separately replaceable, because the interesting cases are a piece missing or
/// written by something else.</para>
/// </summary>
public static class PythonToolCacheFixture
{
    public const string Header = "Signature: 8a477f597d28d172789f06886806bc55";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The folder's name, as the tool names it by default.</summary>
    public static string DirectoryName(PythonToolCache tool) => tool switch
    {
        PythonToolCache.Pytest => ".pytest_cache",
        PythonToolCache.Mypy => ".mypy_cache",
        PythonToolCache.Ruff => ".ruff_cache",
        PythonToolCache.Tox => ".tox",
        PythonToolCache.Nox => ".nox",
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null),
    };

    /// <summary>The configuration file this fixture writes beside the folder.</summary>
    public static string Manifest(PythonToolCache tool) => tool switch
    {
        PythonToolCache.Pytest => "pyproject.toml",
        PythonToolCache.Mypy => "mypy.ini",
        PythonToolCache.Ruff => "ruff.toml",
        PythonToolCache.Tox => "tox.ini",
        PythonToolCache.Nox => "noxfile.py",
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null),
    };

    /// <summary>The tag as the tool writes it on Windows.</summary>
    public static string Tag(PythonToolCache tool) => tool switch
    {
        PythonToolCache.Pytest =>
            $"{Header}\n# This file is a cache directory tag created by pytest.\n"
            + "# For information about cache directory tags, see:\n#\thttps://bford.info/cachedir/spec.html\n",
        PythonToolCache.Mypy =>
            $"{Header}\r\n# This file is a cache directory tag automatically created by mypy.\r\n"
            + "# For information about cache directory tags see https://bford.info/cachedir/\r\n",
        PythonToolCache.Ruff => Header,
        PythonToolCache.Tox =>
            $"{Header}\r\n# This file is a cache directory tag created by tox.\r\n"
            + "# For information about cache directory tags, see:\r\n#\thttps://bford.info/cachedir/spec.html\r\n",
        PythonToolCache.Nox => $"{Header}\r\n",
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null),
    };

    /// <summary>The <c>.gitignore</c> as the tool writes it on Windows.</summary>
    public static string Gitignore(PythonToolCache tool) => tool switch
    {
        PythonToolCache.Pytest => "# Created by pytest automatically.\n*\n",
        PythonToolCache.Mypy => "# Automatically created by mypy\r\n*\r\n",
        PythonToolCache.Ruff => "# Automatically created by ruff.\n*\n",
        PythonToolCache.Tox => "*\r\n",
        PythonToolCache.Nox => "*\r\n",
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null),
    };

    /// <summary>A project the tool's provider recognises, returning its cache folder.</summary>
    public static string Create(string projectDirectory, PythonToolCache tool) =>
        Create(projectDirectory, tool, Tag(tool), Gitignore(tool));

    /// <summary>
    /// A project with the cache folder's files as given, returning the folder. A null text leaves
    /// that file out.
    /// </summary>
    public static string Create(
        string projectDirectory,
        PythonToolCache tool,
        string? tag,
        string? gitignore,
        bool writeManifest = true,
        int payloadBytes = 4096)
    {
        if (writeManifest)
        {
            Write(Path.Combine(projectDirectory, Manifest(tool)), "# configuration\n");
        }

        Write(Path.Combine(projectDirectory, "src", "module.py"), "print('hello')\n");

        var cache = Path.Combine(projectDirectory, DirectoryName(tool));
        Directory.CreateDirectory(LongPath.Extended(cache));

        if (tag is not null)
        {
            Write(Path.Combine(cache, "CACHEDIR.TAG"), tag);
        }

        if (gitignore is not null)
        {
            Write(Path.Combine(cache, ".gitignore"), gitignore);
        }

        var payload = Path.Combine(cache, "v", "results.bin");
        Directory.CreateDirectory(LongPath.Extended(Path.GetDirectoryName(payload)!));
        File.WriteAllBytes(LongPath.Extended(payload), new byte[payloadBytes]);

        return cache;
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(LongPath.Extended(Path.GetDirectoryName(path)!));
        File.WriteAllText(LongPath.Extended(path), text, Utf8);
    }
}
