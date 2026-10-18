using System.Text.Json;
using System.Text.Json.Serialization;

namespace Saem.Contracts;

/// <summary>
/// The contract on the wire. An answer names its case in <c>kind</c> — <c>answered</c> or
/// <c>refused</c> — and is read back through the same constructors that hold its rules in memory, so
/// a payload that would carry an answer with no claim, or a claim with no source, fails while it is
/// read. Members are camelCase, a refusal's reason travels by name, and <c>kind</c> may appear anywhere
/// among the members: a consumer that is not .NET writes them in its own order.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UseStringEnumConverter = true,
    AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(LookupCall))]
[JsonSerializable(typeof(LookupAnswer))]
public sealed partial class ContractJson : JsonSerializerContext;
