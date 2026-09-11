using System.Globalization;
using System.Text.Json;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// A synthetic Claude Code folder, built inside a fake profile, with every id, name and token invented.
///
/// <para>A real Claude Code folder holds conversations, credentials and the account's identity beside
/// the derived state its providers reach into, so no test may read one. Every session id here is a
/// made-up UUID, every project folder is named for an invented path, and every token is a run of
/// zeros.</para>
/// </summary>
public sealed class ClaudeCodeFixture
{
    public const string SessionA = "11111111-1111-4111-8111-111111111111";
    public const string SessionB = "22222222-2222-4222-8222-222222222222";
    public const string SessionC = "33333333-3333-4333-8333-333333333333";

    /// <summary>The second id in a failed-events file's name, which is not a session's.</summary>
    public const string BatchId = "99999999-9999-4999-8999-999999999999";

    public const string ProjectFolder = "C--Users-testuser-src-example";
    public const string OtherProjectFolder = "C--Users-testuser-src-another";

    /// <summary>An editor's connection token. No sentence Deguffer writes may ever contain it.</summary>
    public const string AuthToken = "00000000-0000-4000-8000-00000000abcd";

    /// <summary>Old enough to be past every provider's floor on recent content.</summary>
    public static readonly TimeSpan Old = TimeSpan.FromDays(30);

    public ClaudeCodeFixture(FakeUserEnvironment environment)
        : this(Path.Combine(environment.UserProfile, ".claude"))
    {
    }

    public ClaudeCodeFixture(string home)
    {
        Home = home;
        Directory.CreateDirectory(home);
    }

    public string Home { get; }

    public string Projects => Path.Combine(Home, "projects");

    public string Project(string folder = ProjectFolder) => Path.Combine(Projects, folder);

    public string SessionEnvironments => Path.Combine(Home, "session-env");

    public string Sessions => Path.Combine(Home, "sessions");

    public string Ide => Path.Combine(Home, "ide");

    public string ShellSnapshots => Path.Combine(Home, "shell-snapshots");

    public string Telemetry => Path.Combine(Home, "telemetry");

    public string FileHistory => Path.Combine(Home, "file-history");

    /// <summary>
    /// One session's rewind snapshots. A snapshot keeps the last-write time of the file it copied, so the
    /// snapshots and their folder are aged apart: <paramref name="snapshotAge"/> for the files, and
    /// <paramref name="folderAge"/> for the folder, set last because adding a file moves it.
    /// </summary>
    public string RewindSnapshots(string session, TimeSpan? folderAge = null, TimeSpan? snapshotAge = null)
    {
        var folder = Path.Combine(FileHistory, session);

        foreach (var name in new[] { "0123456789abcdef@v1", "0123456789abcdef@v2" })
        {
            var snapshot = CreateFile(Path.Combine(folder, name), 256);

            if (snapshotAge is { } by)
            {
                TempDirectory.Age(snapshot, by);
            }
        }

        AgeFolder(folder, folderAge);

        return folder;
    }

    public string Transcript(string session, string folder = ProjectFolder) =>
        CreateFile(Path.Combine(Project(folder), session + ".jsonl"), 128);

    /// <summary>
    /// Tool output spilled beside a session's transcript. Dated by the folders, never by the file inside,
    /// which is how a provider has to date it too.
    /// </summary>
    public string SpilledOutput(string session, string folder = ProjectFolder, TimeSpan? age = null)
    {
        var sidecar = Path.Combine(Project(folder), session);
        var output = Path.Combine(sidecar, "tool-results");

        CreateFile(Path.Combine(output, "output.txt"), 512);

        AgeFolder(output, age);
        AgeFolder(sidecar, age);

        return sidecar;
    }

    public string Memory(string folder = ProjectFolder)
    {
        var memory = Path.Combine(Project(folder), "memory");
        CreateFile(Path.Combine(memory, "MEMORY.md"), 64);
        return memory;
    }

    public string HookEnvironment(string session, TimeSpan? age = null)
    {
        var folder = Path.Combine(SessionEnvironments, session);
        Directory.CreateDirectory(folder);
        AgeFolder(folder, age);
        return folder;
    }

    /// <summary>An entry in Claude Code's list of running sessions.</summary>
    public string Registered(int processId, string session, DateTimeOffset? started = null, string? domain = "win32:testmachine")
    {
        var fields = new Dictionary<string, object>
        {
            ["pid"] = processId,
            ["sessionId"] = session,
            ["cwd"] = @"C:\Users\testuser\src\example",
            ["kind"] = "interactive",
        };

        if (started is { } start)
        {
            fields["procStart"] = FileTime(start);
        }

        if (domain is not null)
        {
            fields["pidDomain"] = domain;
        }

        return WriteText(Path.Combine(Sessions, $"{processId}.json"), JsonSerializer.Serialize(fields));
    }

    /// <summary>A process's messaging key: an invented token, and its process's creation time where given.</summary>
    public string MessagingKey(int processId, DateTimeOffset? started = null, string? domain = null)
    {
        var fields = new Dictionary<string, object> { ["peerToken"] = new string('0', 32) };

        if (started is { } start)
        {
            fields["procStartFt"] = FileTime(start);
        }

        if (domain is not null)
        {
            fields["pidDomain"] = domain;
        }

        return WriteText(
            Path.Combine(Sessions, $"{processId}.{new string('a', 64)}.key"), JsonSerializer.Serialize(fields));
    }

    /// <summary>An editor's handshake file, carrying a connection token the way a real one does.</summary>
    public string EditorLock(int port, int processId, bool runningInWindows = true) =>
        WriteText(
            Path.Combine(Ide, $"{port}.lock"),
            JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["pid"] = processId,
                ["workspaceFolders"] = new[] { @"C:\Users\testuser\src\example" },
                ["ideName"] = "Test Editor",
                ["transport"] = "ws",
                ["runningInWindows"] = runningInWindows,
                ["authToken"] = AuthToken,
            }));

    /// <summary>A shell capture, named for the instant it was taken and dated to it.</summary>
    public string ShellSnapshot(DateTime writtenUtc, string suffix = "abc123")
    {
        var path = CreateFile(
            Path.Combine(ShellSnapshots, $"snapshot-bash-{new DateTimeOffset(writtenUtc).ToUnixTimeMilliseconds()}-{suffix}.sh"),
            256);

        File.SetCreationTimeUtc(LongPath.Extended(path), writtenUtc);
        File.SetLastWriteTimeUtc(LongPath.Extended(path), writtenUtc);

        return path;
    }

    /// <summary>Usage events one session could not send.</summary>
    public string FailedEvents(string session, TimeSpan? age = null)
    {
        var path = CreateFile(Path.Combine(Telemetry, $"1p_failed_events.{session}.{BatchId}.json"), 2048);

        if (age is { } by)
        {
            TempDirectory.Age(path, by);
        }

        return path;
    }

    public string CreateFile(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    public string WriteText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// Push a folder's own timestamps back. A folder is dated by these and by its immediate entries', so
    /// a caller ages the entries first and the folder holding them last.
    /// </summary>
    public static void AgeFolder(string path, TimeSpan? age)
    {
        if (age is not { } by)
        {
            return;
        }

        var when = DateTime.UtcNow - by;

        Directory.SetCreationTimeUtc(LongPath.Extended(path), when);
        Directory.SetLastWriteTimeUtc(LongPath.Extended(path), when);
    }

    private static string FileTime(DateTimeOffset instant) =>
        instant.UtcDateTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture);
}
