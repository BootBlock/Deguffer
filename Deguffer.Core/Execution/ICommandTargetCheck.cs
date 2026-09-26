namespace Deguffer.Core.Execution;

/// <summary>
/// Whether a command's arguments still name the item the plan meant, asked by the run immediately
/// before the command.
///
/// <para><b>For a tool that names an item by a handle it reuses.</b> <c>pnputil</c> removes a driver
/// package by the <c>oem&lt;N&gt;.inf</c> name Windows gave it on import, and Windows gives a newly
/// staged package the lowest free number. A package removed between the preview and the clean, by an
/// installer or by Windows Update, frees its number for the next one staged, which can be the newest
/// version of a driver. The command would then remove a package the plan never saw, and §5.6 could
/// not notice, because a folder the plan never listed is not one it protects.</para>
///
/// <para>Implemented by the provider that knows how its tool resolves the handle, so the run holds no
/// knowledge of any tool, for the reason <see cref="IUseCheck"/> gives.</para>
/// </summary>
public interface ICommandTargetCheck
{
    /// <summary>
    /// Why <paramref name="step"/> must not run now, as a lower-case clause with no full stop that
    /// completes "Not run: …", or null where its arguments still name
    /// <see cref="RunCommandStep.Removes"/>. Asked of the machine at the moment of the call.
    /// </summary>
    string? WhyNot(RunCommandStep step, CancellationToken ct);
}
