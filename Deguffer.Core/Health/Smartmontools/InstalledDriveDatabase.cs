using System.Text;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Microsoft.Win32;

namespace Deguffer.Core.Health.Smartmontools;

/// <summary>The installed drive database as last read, or why there is none.</summary>
internal abstract record DatabaseReading
{
    /// <summary>No smartmontools with a database file was found, which the page does not mention.</summary>
    internal static readonly DatabaseReading NotInstalled = new Absent();

    /// <summary>What the database says about <paramref name="drive"/>, or null where there is nothing to say.</summary>
    internal abstract DatabaseAnswer? Of(DriveIdentity drive);

    internal sealed record Loaded(DriveDatabase Database) : DatabaseReading
    {
        internal override DatabaseAnswer Of(DriveIdentity drive) => Database.Find(drive);
    }

    /// <summary>A database file was found and could not be read, so none of it is used.</summary>
    /// <param name="File">The file's name, without its folder.</param>
    /// <param name="Line">The line it failed on, or 0 where it could not be read at all.</param>
    internal sealed record Unreadable(string File, int Line = 0) : DatabaseReading
    {
        internal override DatabaseAnswer Of(DriveIdentity drive) => new DatabaseAnswer.FileUnreadable(File, Line);
    }

    private sealed record Absent : DatabaseReading
    {
        internal override DatabaseAnswer? Of(DriveIdentity drive) => null;
    }
}

/// <summary>
/// smartmontools' drive database, read at run time from the user's own installation and never shipped
/// with Deguffer (#751). smartctl on Windows reads <c>drivedb-add.h</c> and then <c>drivedb.h</c> from
/// the folder it runs from, so the same two files are read from the same folder, in that order. The
/// folder is the one the smartmontools installer records in its uninstall entry, or else the one
/// <c>smartctl</c> is found in on <c>PATH</c>.
///
/// <para>A reading is kept while each file keeps its size and time of last change, so a Health refresh
/// after <c>update-smart-drivedb</c> reads the new database, and one without reads nothing again.</para>
/// </summary>
internal sealed class InstalledDriveDatabase(IUserEnvironment environment)
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\smartmontools";
    private const string InstallLocation = "InstallLocation";
    private const string ProgramFolder = "bin";
    private const string Smartctl = "smartctl";

    /// <summary>Room for a database many times the size of drivedb.h, so that a file that is not one is never held whole in memory.</summary>
    private const int LargestFile = 16 * 1024 * 1024;

    private static readonly string[] FileNames = ["drivedb-add.h", "drivedb.h"];

    private static readonly RegistryView[] Views = [RegistryView.Registry64, RegistryView.Registry32];

    private readonly Lock _gate = new();
    private (DatabaseFile[] Files, DatabaseReading Reading)? _kept;

    /// <summary>The database as it is now. Blocks on the registry and the files, so it is called off the UI thread.</summary>
    internal DatabaseReading Read()
    {
        if (Folder() is not { } folder)
        {
            return DatabaseReading.NotInstalled;
        }

        var files = new List<DatabaseFile>(FileNames.Length);

        foreach (var name in FileNames)
        {
            var path = Path.Combine(folder, name);

            switch (LongPath.ProbeFile(path))
            {
                case PathPresence.Absent:
                    continue;
                case PathPresence.Refused:
                    // A file Windows will not describe may be the one smartctl reads, so the rest are not read without it.
                    return new DatabaseReading.Unreadable(name);
            }

            try
            {
                var info = new FileInfo(LongPath.Extended(path));
                files.Add(new DatabaseFile(path, info.Length, info.LastWriteTimeUtc));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Gone or refused between the probe and the look at its size.
                return new DatabaseReading.Unreadable(name);
            }
        }

        if (files.Count == 0)
        {
            return DatabaseReading.NotInstalled;
        }

        lock (_gate)
        {
            if (_kept is ({ } keptFiles, { } kept) && keptFiles.SequenceEqual(files))
            {
                return kept;
            }

            var reading = Load(files);
            _kept = ([.. files], reading);
            return reading;
        }
    }

    private static DatabaseReading Load(List<DatabaseFile> files)
    {
        var entries = new List<DriveDatabaseEntry>();

        foreach (var file in files)
        {
            var name = Path.GetFileName(file.Path);

            if (Text(file.Path) is not { } text)
            {
                return new DatabaseReading.Unreadable(name);
            }

            if (!DriveDatabaseFile.TryRead(text, out var fileEntries, out var failedLine))
            {
                return new DatabaseReading.Unreadable(name, failedLine);
            }

            entries.AddRange(fileEntries);
        }

        return new DatabaseReading.Loaded(new DriveDatabase(entries));
    }

    /// <summary>The file as text, or null where it is too large, refused, gone or not UTF-8.</summary>
    private static string? Text(string path)
    {
        if (BoundedFile.Read(path, LargestFile) is not { } bytes)
        {
            return null;
        }

        try
        {
            return BoundedFile.StrictUtf8.GetString(bytes.Span);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>The folder smartctl runs from, or null where smartmontools is not found.</summary>
    private string? Folder()
    {
        foreach (var view in Views)
        {
            if (environment.ReadLocalMachineRegistryValue(UninstallKey, InstallLocation, view) is { Length: > 0 } installed)
            {
                return Path.Combine(installed.Trim('"'), ProgramFolder);
            }
        }

        return environment.FindExecutable(Smartctl) is { } smartctl ? Path.GetDirectoryName(smartctl) : null;
    }

    /// <summary>A database file, with the size and time of last change that tell a reading of it is still current.</summary>
    private sealed record DatabaseFile(string Path, long Length, DateTime LastWriteUtc);
}
