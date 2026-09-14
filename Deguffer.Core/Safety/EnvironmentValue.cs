namespace Deguffer.Core.Safety;

/// <summary>
/// One value read from an environment registry key, with the part of it a plain string loses:
/// whether Windows resolves the <c>%NAME%</c> references written in it.
///
/// <para><b>The kind decides what the value means, not merely how it was stored.</b> Windows
/// expands a <c>REG_EXPAND_SZ</c> value when it builds a process's environment, and hands a
/// <c>REG_SZ</c> one to the program exactly as written. A reader that expands both agrees with the
/// tool about a relocated cache only by luck: where the value is a literal
/// <c>%SOMETHING%\cache</c>, the tool is given that string and Deguffer would be given a resolved
/// directory the tool never looks in, then measure and offer to empty it (§5.2).</para>
///
/// <para>Which kind a value has is decided by whatever wrote it, and the two kinds are mixed
/// within one key on an ordinary machine. Nothing can be assumed from the name, the hive or the
/// presence of a <c>%</c>, which is why the kind is read rather than inferred.</para>
/// </summary>
/// <param name="Text">The value as the registry holds it, with nothing expanded.</param>
/// <param name="Expandable">
/// True for <c>REG_EXPAND_SZ</c>. False for <c>REG_SZ</c>, whose <c>%NAME%</c> is part of the value.
/// </param>
internal readonly record struct EnvironmentValue(string Text, bool Expandable);
