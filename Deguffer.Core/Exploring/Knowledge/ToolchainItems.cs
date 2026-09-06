namespace Deguffer.Core.Exploring.Knowledge;

/// <summary>
/// The caches a developer toolchain leaves in a profile, the package trees it leaves beside a
/// project, and the SDKs it installs for the whole machine.
///
/// <para>These are what Deguffer exists for, so the verdicts are the most useful in the catalogue
/// and also the easiest to get wrong. Every one of these tools can say where its own cache is and
/// clear it properly, which is §5.1's rule, so the line names that command rather than telling
/// somebody to delete a folder. Two of these folders are the trap that rule exists for:
/// <c>.npm</c> and <c>.pub-cache</c> in a Windows profile are usually <em>not</em> those tools'
/// caches, and a rule that matched on the familiar name would clear the wrong thing.</para>
///
/// <para>Where a tool's folder holds configuration or credentials beside its cache — Cargo's
/// registry login, Maven's server passwords, Gradle's properties file — the entry says so, because
/// that is the difference between reclaiming space and losing an account.</para>
///
/// <para>The machine-wide SDKs at the foot of the list have a different job, the one
/// <see cref="SharedItems"/>' entries have: they sit under <c>Program Files</c>, which §7.1 refuses
/// outright and no provider reaches, so a reader who has just found several gigabytes there is owed
/// the reason and the supported route rather than a refusal. Every one of them is an installed
/// product, so that route is an uninstaller and the verdict says so. <c>dotnet\packs</c> is the
/// exception and the reason these are here at all: <c>dotnet workload clean</c> is a genuine §5.1
/// eviction command, and it neither reports what it would free before it runs nor frees anything at
/// all where Visual Studio installed the workloads and still holds its claim on them.</para>
/// </summary>
internal static class ToolchainItems
{
    public static IReadOnlyList<KnownItem> All { get; } =
    [
        new(
            KnownPlace.UserProfile,
            @".nuget\packages",
            "Every NuGet package this machine has restored, unpacked rather than compressed, one "
            + "folder per package and version. Projects read straight out of it instead of keeping "
            + "their own copy, and nothing ever prunes it, so it grows for as long as the machine "
            + "is used and tens of gigabytes is unremarkable.",

            "It rebuilds itself from the network, and 'dotnet nuget locals global-packages --clear' "
            + "is the way to empty it — after which every project has to restore again, which "
            + "cannot be done offline."),

        new(
            KnownPlace.UserProfile,
            ".gradle",
            "Gradle's home folder: the dependencies and build output it caches, whole Gradle "
            + "distributions one per version any project has asked for, downloaded Java runtimes, "
            + "and its own configuration. Several gigabytes is normal. Gradle already removes "
            + "caches nothing has used for thirty days on its own.",

            "The caches inside it rebuild from the network, but the initialisation scripts and the "
            + "properties file beside them are configuration that often holds credentials, so this "
            + "folder does not go as a whole."),

        new(
            KnownPlace.UserProfile,
            ".cargo",
            "Cargo's home folder: the crate index, the downloaded crate archives and their "
            + "extracted sources, which is usually the bulk of it. It also holds the tools 'cargo "
            + "install' has put on the path, the configuration, and the registry login.",

            "Cargo restores any cached part from the network, but the 'bin' folder is installed "
            + "programs and the credentials file beside it is a login, so this folder does not go "
            + "as a whole."),

        new(
            KnownPlace.UserProfile,
            ".m2",
            "Maven's per-account folder. Two quite different things live here: the local repository "
            + "of downloaded dependencies, which is the large part, and the settings file, which "
            + "holds repository credentials.",

            "The repository inside it rebuilds from the network and the settings file beside it "
            + "does not, so this folder does not go as a whole."),

        new(
            KnownPlace.UserProfile,
            @".m2\repository",
            "Every Java dependency Maven has downloaded, kept unpacked and shared between projects. "
            + "Nothing prunes it, so it grows for the life of the machine and gigabytes is normal.",

            "Maven downloads whatever a build needs again, so clearing it recovers the space at the "
            + "cost of a slow first build and no builds at all while offline."),

        new(
            KnownPlace.UserProfile,
            ".npm",
            "On Windows this is usually not npm's cache, which lives under 'AppData\\Local' "
            + "instead. A folder of this name in a Windows profile normally comes from WSL, from a "
            + "Unix-style shell, or from a tool that assumed a Unix layout.",

            "Ask npm rather than assume: 'npm config get cache' says where its cache actually is, "
            + "and 'npm cache clean --force' clears that one."),

        new(
            KnownPlace.LocalAppData,
            "npm-cache",
            "npm's cache on Windows: an opaque store of every package archive and web response it "
            + "has fetched. It only grows, and gigabytes is normal on a machine that builds "
            + "JavaScript projects. npm describes it as self-healing, so it does not need clearing "
            + "for any reason except space.",

            "'npm cache clean --force' empties it, and the only cost is that the next install "
            + "downloads everything again."),

        new(
            KnownPlace.LocalAppData,
            @"Pub\Cache",
            "The package cache for Dart and Flutter on Windows: every package version any project "
            + "has depended on, shared between them all. Flutter pulls in a great deal, so hundreds "
            + "of megabytes to a few gigabytes is normal.",

            "'dart pub cache clean' empties it and the packages are downloaded again on the next "
            + "build."),

        new(
            KnownPlace.UserProfile,
            ".pub-cache",
            "The package cache for Dart and Flutter, at the location those tools use on Linux and "
            + "macOS. On Windows the default is under 'AppData\\Local' instead, so this folder "
            + "means the PUB_CACHE setting points here or the profile came from a Unix-shaped "
            + "environment.",

            "'dart pub cache clean' empties whichever one is actually in use, and the packages are "
            + "downloaded again on the next build."),

        new(
            KnownPlace.UserProfile,
            @".dotnet\tools",
            "The .NET command-line tools installed for this account, and put on the command path so "
            + "they can be run from anywhere. These are programs rather than a cache.",

            "Deleting it removes every one of those tools while leaving the path pointing at a "
            + "folder that is gone, so 'dotnet tool uninstall --global <name>' removes one "
            + "properly."),

        new(
            KnownPlace.UserProfile,
            @".vscode\extensions",
            "Every Visual Studio Code extension installed for this account. Extensions that bring "
            + "their own language server, compiler or model are individually large, and older "
            + "versions of one can linger, so a few gigabytes is ordinary.",

            "Deleting a folder here removes an extension without Code knowing, so uninstall it from "
            + "the Extensions view or with 'code --uninstall-extension <id>' instead."),

        new(
            KnownPlace.Anywhere,
            "node_modules",
            "The dependencies of one JavaScript project, installed beside it. It is hundreds of "
            + "megabytes and hundreds of thousands of tiny files for a single project, and a "
            + "machine used for web work carries dozens of them.",

            "A project with a lock file rebuilds it exactly with 'npm ci', so removing it costs a "
            + "reinstall; without one, 'npm install' still rebuilds it from package.json, but the "
            + "versions it resolves may differ from what was there."),

        new(
            KnownPlace.ProgramFiles,
            "dotnet",
            "The .NET installation for the whole machine: the SDKs, the runtimes they build and run "
            + "against, the reference assemblies, and the workload packs for Android, iOS and Mac "
            + "Catalyst. Each SDK and runtime version installs beside the last rather than over it, "
            + "and Visual Studio brings its own, so this holds every version the machine has been "
            + "given and several gigabytes is ordinary.",

            "Each version has its own entry in Settings, under Apps, which is what removes it "
            + "properly — deleting a folder here leaves Windows still recording it as installed."),

        new(
            KnownPlace.ProgramFiles,
            @"dotnet\packs",
            "Two things, side by side. The reference assemblies every .NET project compiles "
            + "against, which arrive with the SDK itself; and the workload packs — the Android, iOS "
            + "and Mac Catalyst SDKs, and the Mono and native runtime packs those platforms run on. "
            + "A full set of workload packs is kept for each version line of the SDK and each "
            + "workload release, and an update adds a set rather than replacing one, so sets for "
            + "version lines no installed SDK still uses collect here. Nothing reports what they "
            + "come to before they are removed.",

            "'dotnet workload clean' is the supported way to remove the workload packs nothing "
            + "installed still uses, and it frees nothing where Visual Studio installed the "
            + "workloads, because the installer's own claim on them stays."),

        new(
            KnownPlace.ProgramFiles,
            @"NVIDIA GPU Computing Toolkit\CUDA",
            "The CUDA Toolkit: the compiler, libraries, headers and profiling tools for writing "
            + "software that runs on an NVIDIA GPU. There is one folder per toolkit version, each "
            + "of them a few gigabytes, and the installer adds a version beside the ones already "
            + "there rather than replacing them, so more than one version is usually present.",

            "A version is removed by its own entries in Settings, under Apps, and deleting its "
            + "folder by hand leaves those entries behind still claiming it is installed."),

        new(
            KnownPlace.ProgramFilesX86,
            @"Windows Kits\10",
            "The Windows SDK: the headers, libraries, metadata and tools for building Windows "
            + "software, together with the Debugging Tools for Windows and the Windows Performance "
            + "Toolkit. Its contents are split by version, one folder per SDK version under "
            + "'Include', 'Lib' and the rest, and installing a newer SDK adds a version rather than "
            + "replacing the one before it, so a machine kept current for a few years carries "
            + "several.",

            "Each version has its own 'Windows Software Development Kit' entry in Settings, under "
            + "Apps, and Microsoft documents no supported way to remove one version's folders by "
            + "hand."),
    ];
}
