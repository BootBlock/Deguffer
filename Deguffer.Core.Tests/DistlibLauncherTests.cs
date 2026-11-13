using System.Text;
using Deguffer.Core.Providers;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// pip's launcher names the interpreter that decides <c>sys.prefix</c>, and so two of the places pip
/// reads its configuration from. Each launcher here is built from bytes as distlib lays one out.
/// </summary>
public sealed class DistlibLauncherTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("#!\"C:\\Program Files\\Python312\\python.exe\"\r\n", @"C:\Program Files\Python312\python.exe")]
    [InlineData("#!\"C:\\Program Files\\Python312\\python.exe\"\n", @"C:\Program Files\Python312\python.exe")]
    [InlineData("#!C:\\Python312\\python.exe\r\n", @"C:\Python312\python.exe")]
    [InlineData("#!C:\\Python312\\python.exe\n", @"C:\Python312\python.exe")]
    [InlineData("#!C:\\Python312\\python.exe -E\r\n", @"C:\Python312\python.exe")]
    [InlineData("#!\"C:\\Program Files\\Python312\\python.exe\" -E\r\n", @"C:\Program Files\Python312\python.exe")]
    public void ReadsTheInterpreterTheShebangBeforeTheArchiveNames(string shebang, string interpreter)
    {
        Assert.Equal(interpreter, DistlibLauncher.InterpreterIn(DistlibLaunchers.Bytes(shebang)));
    }

    [Fact]
    public void ReadsTheLauncherFromDisk()
    {
        var launcher = Path.Combine(_temp.Path, "Scripts", "pip.exe");
        DistlibLaunchers.Write(launcher, @"C:\Python312\python.exe");

        Assert.Equal(@"C:\Python312\python.exe", DistlibLauncher.InterpreterOf(launcher));
    }

    /// <summary>
    /// The archive's own comment is part of its end record, so the record is found where its comment
    /// ends the file, not at a fixed distance from the end.
    /// </summary>
    [Fact]
    public void FindsTheArchiveWhenItEndsWithAComment()
    {
        var archive = DistlibLaunchers.Archive();
        var comment = Encoding.ASCII.GetBytes("a comment");
        archive[^2] = (byte)comment.Length;
        byte[] launcher = [.. Encoding.ASCII.GetBytes("MZ\n#!C:\\Python312\\python.exe\r\n"), .. archive, .. comment];

        Assert.Equal(@"C:\Python312\python.exe", DistlibLauncher.InterpreterIn(launcher));
    }

    [Theory]
    [InlineData("@echo off\r\npython -m pip %*\r\n")]
    [InlineData("")]
    public void NamesNoInterpreterForAFileThatHoldsNoArchive(string content)
    {
        Assert.Null(DistlibLauncher.InterpreterIn(Encoding.ASCII.GetBytes(content)));
    }

    [Theory]
    [InlineData("C:\\Python312\\python.exe\r\n")]
    [InlineData("#!python.exe\r\n")]
    [InlineData("#!/usr/bin/env python\n")]
    [InlineData("#!\"C:\\Program Files\\Python312\\python.exe\r\n")]
    [InlineData("#!C:\\Python312\\python.exe")]
    public void NamesNoInterpreterWhereTheLineBeforeTheArchiveIsNoShebangNamingAFullPath(string line)
    {
        Assert.Null(DistlibLauncher.InterpreterIn(DistlibLaunchers.Bytes(line)));
    }

    [Fact]
    public void NamesNoInterpreterForAShebangThatIsNotUtf8()
    {
        byte[] launcher = [.. Encoding.ASCII.GetBytes("MZ\n#!C:\\Py"), 0xFF, .. Encoding.ASCII.GetBytes("\\python.exe\r\n"), .. DistlibLaunchers.Archive()];

        Assert.Null(DistlibLauncher.InterpreterIn(launcher));
    }

    /// <summary>An archive whose directory claims to start before the file does is no launcher.</summary>
    [Fact]
    public void NamesNoInterpreterWhereTheArchiveWouldStartBeforeTheFile()
    {
        var archive = DistlibLaunchers.Archive();

        Assert.Null(DistlibLauncher.InterpreterIn(archive));
    }

    [Fact]
    public void ReadsNoFileLargerThanAnyLauncher()
    {
        var launcher = Path.Combine(_temp.Path, "pip.exe");
        byte[] padding = new byte[DistlibLauncher.MaximumBytes];
        File.WriteAllBytes(launcher, [.. padding, .. DistlibLaunchers.Bytes("#!C:\\Python312\\python.exe\r\n")]);

        Assert.Null(DistlibLauncher.InterpreterOf(launcher));
    }

    [Fact]
    public void NamesNoInterpreterForAMissingFile()
    {
        Assert.Null(DistlibLauncher.InterpreterOf(Path.Combine(_temp.Path, "absent.exe")));
    }
}
