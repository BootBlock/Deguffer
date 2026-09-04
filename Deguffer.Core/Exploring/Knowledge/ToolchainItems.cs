namespace Deguffer.Core.Exploring.Knowledge;

/// <summary>
/// The caches a developer toolchain leaves in a profile, and the package trees it leaves beside a
/// project.
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
    ];
}
