using System.Text.RegularExpressions;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The inference runtimes LM Studio has superseded: every version of a llama.cpp build it has
/// downloaded, of which it uses one.
///
/// <para>LM Studio downloads a llama.cpp build for each hardware target it supports, and keeps each
/// version of it. Its own clean-up keeps five versions of a line, and only runs after that line
/// downloads again, so a line nothing updates keeps every version it ever had.</para>
///
/// <para><b>§5.1, and what it costs here.</b> <c>lms runtime remove</c> is LM Studio's own command,
/// and it does more than delete a folder: it also removes the shared CUDA and Vulkan libraries that
/// only the removed runtime used. But on Windows it deletes nothing. It marks the folder, and LM
/// Studio deletes it the next time it starts, so the run reports the space as scheduled rather than
/// freed. And <c>lms</c> starts LM Studio when it finds it closed, so this provider asks it nothing
/// unless LM Studio is already running: a preview must not open a program.</para>
///
/// <para><b>§5.2.</b> LM Studio decides what is installed and what is selected, and this provider
/// decides only what is superseded: every version of a line except the newest and any LM Studio
/// selects. A line is recognised only as a llama.cpp build for Windows. Everything else under
/// <c>.lmstudio</c> is Tier 4, the shared libraries under <c>backends\vendor</c> included: a library
/// removed under a live runtime breaks it, and which runtime uses which is LM Studio's to say.</para>
///
/// <para>Tier 2, because selecting a removed runtime again downloads it again, and an old version
/// may not stay published.</para>
/// </summary>
public sealed partial class LmStudioRuntimeProvider : CleanupProviderBase
{
    /// <summary>
    /// What LM Studio writes into a runtime's folder when it removes it later. A folder holding one is
    /// no longer listed, and LM Studio deletes it when it next starts.
    /// </summary>
    public const string Marker = "MARKED_FOR_DELETION";

    /// <summary>
    /// The programs <c>lms</c> talks to: the app, and the service it can run as. The app is first,
    /// because it is the one named to the user.
    /// </summary>
    private static readonly IReadOnlyList<string> LmStudio = ["LM Studio", "llmster"];

    private static readonly ScheduledRemoval MarkedByLmStudio = new(Marker, "LM Studio");

    public LmStudioRuntimeProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        TimeProvider? time = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default,
            time: time)
    {
    }

    public override string Id => "lmstudio-runtimes";

    public override string Name => "LM Studio superseded runtimes";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    /// <summary>Each runtime is a version the user may want to go back to.</summary>
    public override StepGrain Grain => StepGrain.Items;

    public override string WhatHappensOnNextUse =>
        "LM Studio removes these runtimes the next time it starts. The runtime it uses and the newest "
        + "build for each kind of hardware stay. Going back to a removed version downloads it again, "
        + "and an old version may no longer be offered. Your models, chats and settings are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "LM Studio, an app for running language models on your own computer",
        Publisher = "Element Labs",
        Purpose = "LM Studio runs models with llama.cpp, and downloads a build of it for each kind of "
            + "hardware it supports: NVIDIA graphics cards, other graphics cards, and the processor "
            + "alone. It keeps each version it has downloaded, and uses one.",
        Recommendation = "Deguffer keeps the runtime LM Studio uses and the newest build for each kind "
            + "of hardware, and asks LM Studio to remove the rest with its own command. LM Studio must "
            + "be open for Deguffer to ask it.",
    };

    /// <summary>
    /// A llama.cpp build for Windows: <c>llama.cpp-win-</c>, then the architecture and the
    /// accelerator as hyphenated words. A line this does not match is Tier 4, however it is listed.
    /// </summary>
    [GeneratedRegex(@"\Allama\.cpp-win(?:-[A-Za-z0-9_]+)+\z", RegexOptions.CultureInvariant)]
    private static partial Regex RecognisedLine();

    /// <summary>A version as LM Studio numbers its runtimes: two to four dotted numbers.</summary>
    [GeneratedRegex(@"\A[0-9]+(?:\.[0-9]+){1,3}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RecognisedVersion();

    /// <summary>A runtime's folder, which LM Studio names <c>{line}-{version}</c>.</summary>
    [GeneratedRegex(@"\A(?<line>llama\.cpp-win(?:-[A-Za-z0-9_]+)+)-(?<version>[0-9]+(?:\.[0-9]+){1,3})\z", RegexOptions.CultureInvariant)]
    private static partial Regex RuntimeFolder();

    /// <summary>LM Studio's own folder. No variable moves it; only the models folder can be moved.</summary>
    public string Root => Path.Combine(Environment.UserProfile, ".lmstudio");

    /// <summary>Where the runtimes are, each in a folder of its own.</summary>
    public string Backends => Path.Combine(Root, "extensions", "backends");

    /// <summary>LM Studio's command-line tool, which it installs beside its own folder.</summary>
    public string Lms => Path.Combine(Root, "bin", "lms.exe");

    /// <summary>
    /// §5.2 as §7.1 reads it. Neither root recognises a child: a runtime goes only through LM Studio's
    /// own command, and only where LM Studio says it is not selected, which no name can show.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        new ToolRoot(
            Root,
            "This is LM Studio's own folder. It holds your models, chats and settings beside the "
            + "runtimes, so Deguffer removes nothing in it directly.",
            static _ => false),
        new ToolRoot(
            Backends,
            "These are the runtimes LM Studio runs models with, and the libraries they share. Deguffer "
            + "asks LM Studio to remove a runtime it does not use, because nothing about a folder says "
            + "which runtime LM Studio uses.",
            static _ => false),
    ];

    /// <summary>
    /// Read from the disk rather than asked of LM Studio, because asking would start it. Present where
    /// some line holds two or more versions: one version is a working install with nothing to reclaim.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(HoldsSupersededVersions());

    private bool HoldsSupersededVersions()
    {
        if (!LongPath.DirectoryExists(Backends))
        {
            return false;
        }

        var lines = ChildDirectories.Under(Backends).Directories
            .Select(child => (Match: RuntimeFolder().Match(child.Name), child.FullName))
            .Where(folder => folder.Match.Success && !LongPath.FileExists(Path.Combine(folder.FullName, Marker)))
            .GroupBy(folder => folder.Match.Groups["line"].Value, StringComparer.OrdinalIgnoreCase);

        return lines.Any(line => line.Count() > 1);
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        if (NothingToPlanFor(Backends, $"LM Studio has not downloaded any runtimes on this machine ({Backends}).") is { } nothing)
        {
            return nothing;
        }

        if (!LongPath.FileExists(Lms))
        {
            return UnexaminedPlan(
                $"LM Studio's command-line tool is not at {Lms}, so Deguffer cannot ask LM Studio which "
                + "runtimes it uses.");
        }

        if (Inspector.FindRunning(LmStudio).Count == 0)
        {
            return UnexaminedPlan(
                "Open LM Studio, then preview again. Deguffer asks LM Studio which runtimes it uses, and "
                + "LM Studio's command would start LM Studio if it were closed.");
        }

        var listed = await Runner.RunAsync(Lms, "runtime ls", ct).ConfigureAwait(false);

        if (!listed.Succeeded || LmStudioRuntimeList.TryRead(listed.StandardOutput) is not { } runtimes)
        {
            return UnexaminedPlan(
                "LM Studio did not list its runtimes in a form Deguffer can read, so none is offered. "
                + "Which runtime LM Studio uses is what keeps it from being removed.");
        }

        if (!runtimes.Any(runtime => runtime.IsSelected))
        {
            return UnexaminedPlan(
                "LM Studio did not say which runtime it uses, so none is offered.");
        }

        var notes = new List<PlanNote>();
        var kept = new List<(LmStudioRuntime Runtime, string Reason)>();
        var offered = new List<(LmStudioRuntime Runtime, string Folder)>();

        foreach (var line in runtimes.GroupBy(runtime => runtime.Name, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            if (!RecognisedLine().IsMatch(line.Key) || line.Any(runtime => !RecognisedVersion().IsMatch(runtime.Version)))
            {
                // Not protected by name either: a name Deguffer does not recognise is not one it builds a
                // path from, and no step here is sent anywhere near it.
                notes.Add(Information($"Leaving every version of {line.Key} alone: not a runtime Deguffer recognises."));
                continue;
            }

            var newest = line.MaxBy(runtime => Version.Parse(runtime.Version))!;

            foreach (var runtime in line)
            {
                if (runtime.IsSelected)
                {
                    kept.Add((runtime, "The runtime LM Studio uses."));
                }
                else if (runtime == newest)
                {
                    kept.Add((runtime, "The newest build for its kind of hardware."));
                }
                else if (Offerable(runtime, notes) is { } folder)
                {
                    offered.Add((runtime, folder));
                }
            }
        }

        var measured = await MeasureAllAsync([.. offered.Select(item => item.Folder)], ct).ConfigureAwait(false);

        var steps = offered.Zip(measured.Sizes, (item, size) => (CleanupStep)new RunCommandStep(
            Lms,
            // The version is what makes this one runtime: a bare line name removes every version of it.
            $"runtime remove --yes {item.Runtime.Id}",
            // The command beside it names the runtime, and the heading and the version column say it again.
            "Remove this runtime using LM Studio's own command")
        {
            Estimated = size,
            MeasuredPaths = [item.Folder],
            Removes = item.Folder,
            Scheduled = MarkedByLmStudio,
            RunsOnlyWhile = LmStudio,
            Identity = new ItemIdentity(item.Runtime.Id, $"{item.Runtime.Name} {item.Runtime.Version}"),
            Facets = [new ItemFacet("Version", item.Runtime.Version)],
            Group = item.Runtime.Name,
        }).ToList();

        if (steps.Count > 0)
        {
            notes.Add(Information(
                "LM Studio removes these the next time it starts: on Windows its remove command marks a "
                + "runtime rather than deleting it. It also removes the shared libraries that only a "
                + "removed runtime used."));
        }

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            ProtectedPaths = BuildProtectedPaths(kept),
            Notes = notes,
            Fallback = measured.Fallback,
        };
    }

    /// <summary>
    /// The folder of a superseded runtime, or null where it cannot be offered, with the reason noted.
    /// Named by LM Studio's own convention and then looked at, because the command is told a name and
    /// the figure is measured from a folder, and the two have to be the same runtime.
    /// </summary>
    private string? Offerable(LmStudioRuntime runtime, List<PlanNote> notes)
    {
        var folder = Path.Combine(Backends, $"{runtime.Name}-{runtime.Version}");

        switch (LongPath.ProbeDirectory(folder, out var isLink))
        {
            case PathPresence.Absent:
                notes.Add(Information(
                    $"Leaving {runtime.Id} alone: LM Studio lists it, but its folder is not where LM Studio keeps runtimes."));
                return null;

            case PathPresence.Refused:
                notes.Add(UnreadableRoot.UnreachedNote(folder));
                return null;
        }

        if (isLink is not false)
        {
            notes.Add(Information(
                $"Leaving {runtime.Id} alone: its folder is a link to somewhere else, and Deguffer does not measure through a link."));
            return null;
        }

        if (LongPath.FileExists(Path.Combine(folder, Marker)))
        {
            notes.Add(Information(
                $"Leaving {runtime.Id} alone: LM Studio has already marked it, and removes it the next time it starts."));
            return null;
        }

        return LongPath.Display(folder);
    }

    /// <summary>
    /// §5.6. The runtimes kept are the point: the one LM Studio uses and the newest of each line must
    /// survive every command sent to its siblings, whose folders are the same shape beside them. The
    /// shared libraries in <c>vendor</c> stay present however many LM Studio marks, because it marks
    /// the libraries inside and never the folder.
    /// </summary>
    private IReadOnlyList<ProtectedPath> BuildProtectedPaths(IReadOnlyList<(LmStudioRuntime Runtime, string Reason)> kept) => Protect(
    [
        (Root, "LM Studio's own folder must survive: only superseded runtimes inside it are removed."),
        (Backends, "Where LM Studio keeps its runtimes: runtimes are removed from it, never the folder."),
        (Path.Combine(Backends, "vendor"), "The libraries LM Studio's runtimes share."),
        (Path.Combine(Root, "models"), "Your downloaded models."),
        (Path.Combine(Root, ".internal"), "LM Studio's own record of what is installed and selected."),
        .. kept.Select(item => (Path.Combine(Backends, $"{item.Runtime.Name}-{item.Runtime.Version}"), item.Reason)),
    ]);

    private static PlanNote Information(string message) => new(PlanNoteSeverity.Information, message);
}
