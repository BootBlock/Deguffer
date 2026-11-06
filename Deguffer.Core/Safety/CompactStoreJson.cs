using System.Text.Json.Serialization;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Safety;

/// <summary>
/// The contracts of the records Deguffer keeps for itself and nobody edits, written without
/// indentation because they hold an entry for every path measured or refused. Generated at build time
/// for the reason <see cref="IndentedStoreJson"/> gives.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(Dictionary<string, string[]>), TypeInfoPropertyName = "RefusedPlaces")]
[JsonSerializable(typeof(Dictionary<string, ScanEstimateCache.Entry>), TypeInfoPropertyName = "ScanEstimates")]
internal sealed partial class CompactStoreJson : JsonSerializerContext;
