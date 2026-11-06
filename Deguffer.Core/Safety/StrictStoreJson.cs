using System.Text.Json.Serialization;
using Deguffer.Core.Execution.Sizes;
using Deguffer.Core.Providers;

namespace Deguffer.Core.Safety;

/// <summary>
/// The contracts of the versioned records that are read whole or not at all: a member that is missing,
/// unknown or null where the type says it cannot be makes the file one this build does not read.
/// Generated at build time for the reason <see cref="IndentedStoreJson"/> gives.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(ProviderSizeStore.Stored), TypeInfoPropertyName = "ProviderSizes")]
[JsonSerializable(typeof(ComponentStoreSettlement.Stored), TypeInfoPropertyName = "ComponentStoreStates")]
internal sealed partial class StrictStoreJson : JsonSerializerContext;
