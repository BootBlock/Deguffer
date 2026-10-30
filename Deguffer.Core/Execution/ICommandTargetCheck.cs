namespace Deguffer.Core.Execution;

/// <summary>
/// Whether a command, run now, still reaches only what the plan meant, asked by the run immediately
/// before the command.
///
/// <para><b>For a tool that names an item by a handle it reuses.</b> <c>pnputil</c> removes a driver
/// package by the <c>oem&lt;N&gt;.inf</c> name Windows gave it on import, and Windows gives a newly
/// staged package the lowest free number. A package removed between the preview and the clean, by an
/// installer or by Windows Update, frees its number for the next one staged, which can be the newest
/// version of a driver. The command would then remove a package the plan never saw.</para>
///
/// <para><b>And for a tool that decides what to remove when it runs.</b> <c>scoop cache rm *</c>
/// removes whatever its cache folder holds, Vagrant's prune ranks the installed versions afresh, and
/// <c>conan cache clean -t</c> removes every unfinished build in whichever store its configuration
/// names that day. A file saved into the cache, a version installed, a build started or a setting
/// edited after the preview gives the same arguments something new to remove.</para>
///
/// <para>Either way §5.6 could not notice, because what the plan never listed is not something it
/// protects. The check is implemented by the provider that knows how its tool resolves what it
/// removes, so the run holds no knowledge of any tool, for the reason <see cref="IUseCheck"/>
/// gives.</para>
/// </summary>
public interface ICommandTargetCheck
{
    /// <summary>
    /// Why <paramref name="step"/> must not run now, as a lower-case clause with no full stop that
    /// completes "Not run: …", or null where its arguments still reach only what the plan meant: the
    /// item <see cref="RunCommandStep.Removes"/> names, or what the plan listed where the command was
    /// sent. Asked of the machine at the moment of the call, and more than once for one run: before
    /// the run's look on the disk, and again immediately before the command, because the look can
    /// take minutes.
    /// </summary>
    string? WhyNot(RunCommandStep step, CancellationToken ct);
}
