using System.Text.Json;
using System.Text.Json.Serialization;
using Deguffer.Core.Configuration;

namespace Deguffer.Core.Safety;

/// <summary>
/// The contracts of the settings files a user may open and edit by hand, written indented with the
/// serialiser's defaults otherwise.
///
/// <para><b>Generated at build time, never built by reflection at run time.</b> Most of these files
/// are read before the window's first frame, and a reflection contract is built on its type's first
/// use, which ReadyToRun cannot compile ahead. A type is read or written only through the contract
/// declared here, so a store and the file it reads cannot disagree about a member. The build refuses
/// a call that would fall back to reflection: see the trim analyzer in <c>Deguffer.Core.csproj</c>.</para>
///
/// <para>One context for each set of options, because a context's options are fixed when it is
/// generated. <see cref="CompactStoreJson"/> and <see cref="StrictStoreJson"/> hold the other sets.
/// <c>preferences.json</c> is not here yet, and <see cref="Configuration.PreferenceStore"/> says why.</para>
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(string[]), TypeInfoPropertyName = "Folders")]
[JsonSerializable(typeof(IReadOnlyList<string>), TypeInfoPropertyName = "FolderList")]
[JsonSerializable(typeof(Dictionary<string, RememberedSelection>), TypeInfoPropertyName = "Selections")]
[JsonSerializable(typeof(IReadOnlyDictionary<string, RememberedSelection>), TypeInfoPropertyName = "SelectionMap")]
[JsonSerializable(typeof(JsonElement[]), TypeInfoPropertyName = "SourceRootEntries")]
[JsonSerializable(typeof(IEnumerable<SourceRootStore.StoredRoot>), TypeInfoPropertyName = "SourceRoots")]
[JsonSerializable(typeof(IReadOnlyList<KeptItem>), TypeInfoPropertyName = "KeptItems")]
[JsonSerializable(typeof(WindowMetrics))]
[JsonSerializable(typeof(ToolWindowMetrics))]
internal sealed partial class IndentedStoreJson : JsonSerializerContext;
