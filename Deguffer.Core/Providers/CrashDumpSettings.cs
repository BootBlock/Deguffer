using Deguffer.Core.Safety;
using Microsoft.Win32;

namespace Deguffer.Core.Providers;

/// <summary>
/// One place this machine's settings tell Windows to write crash dumps.
/// </summary>
/// <param name="Setting">The setting, as a user would find it in the registry.</param>
/// <param name="Holds">What Windows writes there, as a phrase that can follow "sends".</param>
/// <param name="Value">The value as the registry gave it, with its variables expanded.</param>
/// <param name="NamesFile">
/// Whether the value names the dump file itself rather than the folder dumps are written into.
/// </param>
/// <param name="RequiresElevation">
/// Whether what Windows writes there is written by the kernel, and so is removable only by an
/// administrator. Dumps of ordinary applications are written as the account the application ran as.
/// </param>
public sealed record CrashDumpSetting(
    string Setting,
    string Holds,
    string Value,
    bool NamesFile,
    bool RequiresElevation)
{
    /// <summary>The value as a full path, or null where it is not one.</summary>
    public string? Path => LongPath.Configured(Value);
}

/// <summary>
/// Where this machine's settings tell Windows to write crash dumps, read from the registry.
///
/// <para>Each setting defaults to one of the locations <see cref="CrashDumpProvider"/> declares, and
/// Windows itself writes the kernel's two values on every machine, set to those defaults. A setting
/// that still names its default is read here like any other. Telling the two apart is the
/// provider's question, because only it knows what it declares.</para>
///
/// <para>The native view, because both readers are part of Windows: the kernel reads
/// <c>CrashControl</c> and Windows Error Reporting reads <c>LocalDumps</c> in the view of the
/// operating system, whichever process crashed. On a 32-bit Windows the 64-bit view is the only
/// one.</para>
/// </summary>
internal static class CrashDumpSettings
{
    public const string CrashControlKey = @"SYSTEM\CurrentControlSet\Control\CrashControl";

    public const string LocalDumpsKey = @"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps";

    public const string DumpFileValue = "DumpFile";

    public const string MinidumpDirValue = "MinidumpDir";

    public const string DumpFolderValue = "DumpFolder";

    private const RegistryView View = RegistryView.Registry64;

    /// <summary>Every dump location a setting names, in the order a reader of the registry meets them.</summary>
    public static IReadOnlyList<CrashDumpSetting> Read(IUserEnvironment environment)
    {
        var settings = new List<CrashDumpSetting>();

        Add(settings, environment, CrashControlKey, DumpFileValue, "the full kernel dump", namesFile: true, requiresElevation: true);
        Add(settings, environment, CrashControlKey, MinidumpDirValue, "small kernel dumps", namesFile: false, requiresElevation: true);
        Add(settings, environment, LocalDumpsKey, DumpFolderValue, "application crash dumps", namesFile: false, requiresElevation: false);

        // Each key is named after the executable it applies to, and its folder replaces the one above
        // for that application alone. A key with no folder of its own uses the one above.
        foreach (var application in environment.ReadLocalMachineRegistrySubKeyNames(LocalDumpsKey, View))
        {
            Add(
                settings,
                environment,
                $@"{LocalDumpsKey}\{application}",
                DumpFolderValue,
                $"crash dumps of {application}",
                namesFile: false,
                requiresElevation: false);
        }

        return settings;
    }

    private static void Add(
        List<CrashDumpSetting> settings,
        IUserEnvironment environment,
        string keyPath,
        string valueName,
        string holds,
        bool namesFile,
        bool requiresElevation)
    {
        if (environment.ReadLocalMachineRegistryValue(keyPath, valueName, View) is not { } value
            || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        settings.Add(new CrashDumpSetting(
            $@"HKEY_LOCAL_MACHINE\{keyPath}\{valueName}",
            holds,
            value.Trim(),
            namesFile,
            requiresElevation));
    }
}
