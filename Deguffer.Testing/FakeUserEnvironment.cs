using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;
using Microsoft.Win32;

namespace Deguffer.Testing;

/// <summary>
/// A profile rooted in a temp directory, so provider rules can be asserted against a tree we
/// build rather than the developer's real caches.
/// </summary>
public sealed class FakeUserEnvironment : IUserEnvironment
{
    private readonly Dictionary<string, string> _executables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _variables = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>What a started tool reads in place of <see cref="_variables"/>, where null is unset.</summary>
    private readonly Dictionary<string, string?> _toolVariables = new(StringComparer.OrdinalIgnoreCase);
    private readonly FakeUserHive _hive = new();
    private readonly Dictionary<string, string> _machineRegistry = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _movedPersonalFolders = [];

    public FakeUserEnvironment(string root)
    {
        UserProfile = Path.Combine(root, "profile");
        LocalAppData = Path.Combine(root, "profile", "AppData", "Local");
        RoamingAppData = Path.Combine(root, "profile", "AppData", "Roaming");
        LocalLowAppData = Path.Combine(root, "profile", "AppData", "LocalLow");
        Videos = Path.Combine(root, "profile", "Videos");
        Documents = Path.Combine(root, "profile", "Documents");
        TempPath = Path.Combine(root, "temp");

        Directory.CreateDirectory(UserProfile);
        Directory.CreateDirectory(LocalAppData);
        Directory.CreateDirectory(RoamingAppData);
        Directory.CreateDirectory(LocalLowAppData);
        Directory.CreateDirectory(TempPath);
    }

    public string UserProfile { get; }

    public string LocalAppData { get; }

    public string RoamingAppData { get; }

    public string? LocalLowAppData { get; private set; }

    /// <summary>
    /// Named but not created: a Videos folder is not on every profile, and a provider that reaches into
    /// it must find it absent as easily as present.
    /// </summary>
    public string? Videos { get; private set; }

    public string? Documents { get; private set; }

    /// <summary>
    /// The account's own folders at their default places in the profile, with whatever
    /// <see cref="WithPersonalFolderAt"/> has moved one to. Named but not created, as
    /// <see cref="Videos"/> is.
    /// </summary>
    public IReadOnlyList<string> PersonalFolders =>
    [
        .. new[] { "Desktop", "Downloads", "Music", "Pictures", "Saved Games", "OneDrive" }
            .Select(name => Path.Combine(UserProfile, name)),
        .. new[] { Documents, Videos }.OfType<string>(),
        .. _movedPersonalFolders,
    ];

    public string TempPath { get; private set; }

    /// <summary>Where Windows puts it in a profile. Named but not created, as <see cref="Videos"/> is.</summary>
    public string? StartupFolder => Path.Combine(RoamingAppData, "Microsoft", "Windows", "Start Menu", "Programs", "Startup");

    /// <summary>Where Windows puts it in a profile. Named but not created.</summary>
    public string? StartMenuFolder => Path.Combine(RoamingAppData, "Microsoft", "Windows", "Start Menu");

    /// <summary>Where Windows puts it in a profile. Named but not created.</summary>
    public string? DesktopFolder => Path.Combine(UserProfile, "Desktop");

    /// <summary>
    /// Pretend this process resolves its temporary folder somewhere other than the profile's own.
    ///
    /// <para>Exists because <c>Path.GetTempPath</c> answers from the environment, so the folder a
    /// process actually uses and the profile's default location are genuinely different directories
    /// on a machine that redirects one — a Remote Desktop session host hands out a numbered folder
    /// inside the default, and that pairing is what
    /// <c>TempDirectoryProviderTests.RefusesATemporaryFolderThatNestsWithOneAlreadyAccepted</c>
    /// needs to reproduce. A fixture that could only ever return the one path could not express
    /// it.</para>
    /// </summary>
    public FakeUserEnvironment WithTempPath(string path)
    {
        Directory.CreateDirectory(path);
        TempPath = path;

        return this;
    }

    /// <summary>
    /// A recognisably invented identifier. Real enough in shape for a provider that matches on it,
    /// and nobody's actual SID.
    ///
    /// <para>A constant as well as the default, because a fake standing in for something that acts
    /// on this account's own directory has to name the same account the environment does. Two
    /// copies of the literal would agree until one of them was edited.</para>
    /// </summary>
    public const string SecurityIdentifier = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    public string? UserSecurityIdentifier { get; private set; } = SecurityIdentifier;

    /// <summary>
    /// A recognisably invented account name and machine name, on the same terms as
    /// <see cref="SecurityIdentifier"/>: a fixture that laid out a File History target has to name
    /// the same account and the same machine the provider will look for.
    /// </summary>
    public const string Account = "testuser";

    public const string Machine = "TESTMACHINE";

    public string UserName { get; } = Account;

    public string MachineName { get; } = Machine;

    /// <summary>A recognisably invented domain, on the terms <see cref="Account"/> gives.</summary>
    public const string Domain = "TESTDOMAIN";

    public string DomainName { get; } = Domain;

    /// <summary>
    /// Not an administrator unless a test says so, so whether the suite runs elevated changes nothing
    /// a test measures.
    /// </summary>
    public bool IsAdministrator { get; private set; }

    /// <summary>Pretend the process runs with an administrator's rights.</summary>
    public FakeUserEnvironment AsAdministrator()
    {
        IsAdministrator = true;
        IsHyperVAdministrator = true;
        return this;
    }

    /// <summary>Not one Hyper-V answers unless a test says so, for the reason <see cref="IsAdministrator"/> gives.</summary>
    public bool IsHyperVAdministrator { get; private set; }

    /// <summary>Pretend the process runs unelevated as a member of Hyper-V Administrators.</summary>
    public FakeUserEnvironment AsHyperVAdministrator()
    {
        IsHyperVAdministrator = true;
        return this;
    }

    /// <summary>
    /// No table to read, as an unelevated process finds, unless a test sets one, so whether the suite
    /// runs elevated changes nothing a test measures.
    /// </summary>
    public IMftSourceFactory VolumeTables { get; set; } = FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated);

    /// <summary>
    /// Pretend the account is unidentifiable, which is how a provider that keys on the SID is shown
    /// to fail closed. Set before the provider is constructed: providers read the identity once,
    /// because a process cannot change the account it runs as.
    /// </summary>
    public FakeUserEnvironment WithNoSecurityIdentifier()
    {
        UserSecurityIdentifier = null;
        return this;
    }

    /// <summary>
    /// Pretend Windows would not say where LocalLow is, which is how a provider that reaches into
    /// it is shown to fail closed. The directory itself is left on disk, so the test can prove the
    /// provider declined a cache that was really there rather than one that was simply absent.
    /// </summary>
    public FakeUserEnvironment WithNoLocalLow()
    {
        LocalLowAppData = null;
        return this;
    }

    /// <summary>Pretend Windows would not say where the Videos folder is.</summary>
    public FakeUserEnvironment WithNoVideos()
    {
        Videos = null;
        return this;
    }

    /// <summary>
    /// Pretend Windows has moved one of the account's own folders to <paramref name="path"/>, as a
    /// redirection or OneDrive's folder backup does. Not created, so a test decides what is there.
    /// </summary>
    public FakeUserEnvironment WithPersonalFolderAt(string path)
    {
        _movedPersonalFolders.Add(path);
        return this;
    }

    public FakeUserEnvironment WithNoDocuments()
    {
        Documents = null;
        return this;
    }

    /// <summary>Pretend <paramref name="command"/> is installed at a plausible path.</summary>
    public FakeUserEnvironment WithExecutable(string command, string? path = null)
    {
        _executables[command] = path ?? Path.Combine(@"C:\tools", command + ".exe");
        return this;
    }

    /// <summary>Pretend <paramref name="name"/> is set in the environment.</summary>
    public FakeUserEnvironment WithEnvironmentVariable(string name, string value)
    {
        _variables[name] = value;
        return this;
    }

    /// <summary>
    /// Pretend <paramref name="valueName"/> is recorded under <paramref name="keyPath"/> in the signed-in
    /// user's hive.
    /// </summary>
    public FakeUserEnvironment WithRegistryValue(string keyPath, string valueName, string value)
    {
        _hive.WithValue(keyPath, valueName, value);
        return this;
    }

    /// <summary>Pretend the <c>REG_DWORD</c> <paramref name="valueName"/> is recorded under <paramref name="keyPath"/>.</summary>
    public FakeUserEnvironment WithRegistryNumber(string keyPath, string valueName, int value)
    {
        _hive.WithNumber(keyPath, valueName, value);
        return this;
    }

    /// <summary>Pretend the <c>REG_BINARY</c> <paramref name="valueName"/> is recorded under <paramref name="keyPath"/>.</summary>
    public FakeUserEnvironment WithRegistryBytes(string keyPath, string valueName, byte[] value)
    {
        _hive.WithBytes(keyPath, valueName, value);
        return this;
    }

    public IUserHive UserHive => _hive;

    public IUserHive? RunningAccountHive { get; private set; }

    /// <summary>
    /// The signed-in user is the account the process runs as unless a test says otherwise, so the suite
    /// plans as the ordinary case does.
    /// </summary>
    public SignedInAccount? SessionUser { get; private set; } = new($@"{Domain}\{Account}", SecurityIdentifier);

    /// <summary>
    /// A recognisably invented second account, on the terms <see cref="SecurityIdentifier"/> gives: the
    /// user signed in to Windows while the process runs as <see cref="Account"/>.
    /// </summary>
    public const string SignedInSecurityIdentifier = "S-1-5-21-1111111111-2222222222-3333333333-1002";

    public const string SignedInName = $@"{Domain}\signedinuser";

    /// <summary>
    /// Pretend the process was elevated as another account: <see cref="SignedInName"/> is signed in to
    /// the session, and everything else this environment says is the account the process runs as.
    /// <paramref name="runningHive"/> is that account's own hive, and <see cref="UserHive"/> stays the
    /// signed-in user's. Set before the planner is constructed, which reads the account once.
    /// </summary>
    public FakeUserEnvironment ElevatedAsAnotherAccount(FakeUserHive? runningHive = null)
    {
        SessionUser = new SignedInAccount(SignedInName, SignedInSecurityIdentifier);
        RunningAccountHive = runningHive ?? new FakeUserHive();
        return this;
    }

    /// <summary>Pretend <paramref name="signedIn"/> is signed in to the session.</summary>
    public FakeUserEnvironment WithSessionUser(SignedInAccount signedIn)
    {
        SessionUser = signedIn;
        return this;
    }

    /// <summary>Pretend Windows would not say who is signed in to the session.</summary>
    public FakeUserEnvironment WithNoSessionUser()
    {
        SessionUser = null;
        return this;
    }

    /// <summary>
    /// Pretend <paramref name="valueName"/> is recorded under <paramref name="keyPath"/> in the machine
    /// hive's <paramref name="view"/>. Keyed by the view, so a provider that asks the wrong one is
    /// told nothing, as it would be on a real machine.
    /// </summary>
    public FakeUserEnvironment WithMachineRegistryValue(string keyPath, string valueName, string value, RegistryView view)
    {
        _machineRegistry[MachineKey(keyPath, valueName, view)] = value;
        return this;
    }

    /// <summary>How many times a provider read the environment. Proves a discovery is memoised.</summary>
    public int EnvironmentReads { get; private set; }

    /// <summary>How many times a provider read the registry. Proves a discovery is memoised.</summary>
    public int RegistryReads => _hive.Reads + _machineReads;

    private int _machineReads;

    public string? ReadLocalMachineRegistryValue(string keyPath, string valueName, RegistryView view)
    {
        _machineReads++;

        return _machineRegistry.TryGetValue(MachineKey(keyPath, valueName, view), out var value) ? value : null;
    }

    /// <summary>
    /// Every key a recorded machine value sits under, one level below <paramref name="keyPath"/> in
    /// <paramref name="view"/>, read as <see cref="FakeUserHive.ReadSubKeyNames"/> reads a user's.
    /// </summary>
    public IReadOnlyList<string> ReadLocalMachineRegistrySubKeyNames(string keyPath, RegistryView view)
    {
        _machineReads++;

        var prefix = $"{view}:{keyPath}\\";

        return
        [
            .. _machineRegistry.Keys
                .Where(entry => entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry[prefix.Length..].Split('\\'))
                .Where(segments => segments.Length > 1)
                .Select(segments => segments[0])
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }

    private static string MachineKey(string keyPath, string valueName, RegistryView view) =>
        $"{view}:{keyPath}\\{valueName}";

    public string? GetEnvironmentVariable(string name)
    {
        EnvironmentReads++;

        return _variables.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>
    /// A variable set with <see cref="WithEnvironmentVariable"/> reaches a started tool too, as it does
    /// on a machine nobody changed since Deguffer started. <see cref="WithToolEnvironmentVariable"/>
    /// sets one only a started tool still sees.
    /// </summary>
    public string? GetToolEnvironmentVariable(string name) =>
        _toolVariables.TryGetValue(name, out var value) ? value
        : _variables.TryGetValue(name, out value) ? value
        : null;

    /// <summary>Every variable <see cref="GetToolEnvironmentVariable"/> reads as set, as it stands when asked.</summary>
    public IReadOnlyDictionary<string, string> ToolEnvironment
    {
        get
        {
            var given = new Dictionary<string, string>(_variables, StringComparer.OrdinalIgnoreCase);

            foreach (var (name, value) in _toolVariables)
            {
                if (value is null)
                {
                    given.Remove(name);
                }
                else
                {
                    given[name] = value;
                }
            }

            return given;
        }
    }

    /// <summary>Pretend Deguffer started with <paramref name="name"/> set, and the machine has since dropped it.</summary>
    public FakeUserEnvironment WithToolEnvironmentVariable(string name, string value)
    {
        _toolVariables[name] = value;
        return this;
    }

    /// <summary>Pretend the machine set <paramref name="name"/> after Deguffer started, so only a tool the user starts now sees it.</summary>
    public FakeUserEnvironment WithEnvironmentVariableSetSinceStart(string name, string value)
    {
        _variables[name] = value;
        _toolVariables[name] = null;
        return this;
    }

    /// <summary>
    /// The variables <see cref="GetEnvironmentVariable"/> reads, wherever a started tool reads one of
    /// them differently, as it does on a machine changed since Deguffer started.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ChangedEnvironment =>
        _toolVariables.Count == 0 ? null : new Dictionary<string, string>(_variables, StringComparer.OrdinalIgnoreCase);

    /// <summary>The variables <see cref="GetEnvironmentVariable"/> reads, as they stand when asked.</summary>
    public IReadOnlyDictionary<string, string> CurrentEnvironment =>
        new Dictionary<string, string>(_variables, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The same as <see cref="CurrentEnvironment"/>: a fake's variables change the moment a test sets
    /// them, with nothing kept from a pass, so a test changes the machine after the preview by setting one.
    /// </summary>
    public IReadOnlyDictionary<string, string> ReadEnvironmentNow() => CurrentEnvironment;

    public int InvalidateCount { get; private set; }

    public string? FindExecutable(string command) =>
        _executables.TryGetValue(command, out var path) ? path : null;

    public void Invalidate() => InvalidateCount++;
}
