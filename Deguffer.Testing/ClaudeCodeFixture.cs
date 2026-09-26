using System.Globalization;
using System.Text.Json;
using Deguffer.Core.Safety;

namespace Deguffer.Testing;

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

    /// <summary>The folder a session in <see cref="ProjectFolder"/> records it was started in.</summary>
    public const string ProjectPath = @"C:\Users\testuser\src\example";

    /// <summary>The folder a session in <see cref="OtherProjectFolder"/> records it was started in.</summary>
    public const string OtherProjectPath = @"C:\Users\testuser\src\another";

    /// <summary>The prompt every invented conversation ends on. No title or sentence may ever contain it.</summary>
    public const string LastPrompt = "an invented last prompt that must never be shown";

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
    /// A conversation in the shape Claude Code writes one, with every message invented: a queued prompt,
    /// the first user line recording <paramref name="project"/>, a generated title early on,
    /// <paramref name="padding"/> bytes of assistant lines, any later titles, and the last prompt.
    /// </summary>
    /// <param name="project">The folder the session records, or null for a conversation that records none.</param>
    /// <param name="title">The generated title near the head, or null for none.</param>
    /// <param name="lateTitle">A generated title near the end, superseding <paramref name="title"/>.</param>
    /// <param name="customTitle">A title the user gave the session, near the end.</param>
    /// <param name="age">How long ago it was last written, or null for now.</param>
    public string Conversation(
        string session,
        string folder = ProjectFolder,
        string? project = ProjectPath,
        string? title = "Tidy the build scripts",
        string? lateTitle = null,
        string? customTitle = null,
        DateTimeOffset? started = null,
        TimeSpan? age = null,
        int padding = 0)
    {
        var start = started ?? DateTimeOffset.UtcNow - (age ?? TimeSpan.Zero) - TimeSpan.FromHours(2);
        var stamp = start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var lines = new List<object>
        {
            new Dictionary<string, object> { ["type"] = "queue-operation", ["operation"] = "enqueue", ["timestamp"] = stamp, ["sessionId"] = session },
        };

        var user = new Dictionary<string, object>
        {
            ["type"] = "user",
            ["sessionId"] = session,
            ["timestamp"] = stamp,
            ["isSidechain"] = false,
            ["message"] = new Dictionary<string, object> { ["role"] = "user", ["content"] = "an invented first prompt" },
        };

        if (project is not null)
        {
            user["cwd"] = project;
        }

        lines.Add(user);

        if (title is not null)
        {
            lines.Add(new Dictionary<string, object> { ["type"] = "ai-title", ["aiTitle"] = title, ["sessionId"] = session });
        }

        for (var written = 0; written < padding; written += 1024)
        {
            lines.Add(new Dictionary<string, object>
            {
                ["type"] = "assistant",
                ["sessionId"] = session,
                ["message"] = new Dictionary<string, object> { ["role"] = "assistant", ["content"] = new string('x', 1000) },
            });
        }

        if (lateTitle is not null)
        {
            lines.Add(new Dictionary<string, object> { ["type"] = "ai-title", ["aiTitle"] = lateTitle, ["sessionId"] = session });
        }

        if (customTitle is not null)
        {
            lines.Add(new Dictionary<string, object> { ["type"] = "custom-title", ["customTitle"] = customTitle, ["sessionId"] = session });
        }

        lines.Add(new Dictionary<string, object> { ["type"] = "last-prompt", ["lastPrompt"] = LastPrompt, ["sessionId"] = session });

        var path = WriteText(
            Path.Combine(Project(folder), session + ".jsonl"),
            string.Concat(lines.Select(line => JsonSerializer.Serialize(line) + "\n")));

        if (age is { } by)
        {
            TempDirectory.Age(path, by);
        }

        return path;
    }

    /// <summary>
    /// A session's folder beside its conversation: one subagent's conversation and one spilled output,
    /// dated by the folders, which is how a provider has to date it.
    /// </summary>
    public string SessionFolder(string session, string folder = ProjectFolder, TimeSpan? age = null)
    {
        var sidecar = Path.Combine(Project(folder), session);
        var subagents = Path.Combine(sidecar, "subagents");
        var output = Path.Combine(sidecar, "tool-results");

        foreach (var file in new[]
                 {
                     CreateFile(Path.Combine(subagents, "agent-0123456789abcdef.jsonl"), 512),
                     CreateFile(Path.Combine(output, "output.txt"), 256),
                 })
        {
            if (age is { } by)
            {
                TempDirectory.Age(file, by);
            }
        }

        AgeFolder(subagents, age);
        AgeFolder(output, age);
        AgeFolder(sidecar, age);

        return sidecar;
    }

    /// <summary>Claude Code's own settings file, written as given.</summary>
    public string Settings(string json) => WriteText(Path.Combine(Home, "settings.json"), json);

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
    /// <param name="project">The folder the process was started in, or null for an entry that records none.</param>
    public string Registered(
        int processId,
        string session,
        DateTimeOffset? started = null,
        string? domain = "win32:testmachine",
        string? project = ProjectPath)
    {
        var fields = new Dictionary<string, object>
        {
            ["pid"] = processId,
            ["sessionId"] = session,
            ["kind"] = "interactive",
        };

        if (project is not null)
        {
            fields["cwd"] = project;
        }

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
