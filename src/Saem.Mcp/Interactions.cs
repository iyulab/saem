using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Saem.Contracts;

namespace Saem.Mcp;

/// <summary>How the interaction log names the records a lookup touched.</summary>
public sealed class AuditOptions
{
    /// <summary>
    /// The deployment's secret for naming records in the log: a record key is logged as an HMAC under it,
    /// never as itself. A business key is guessable (a part number, an employee number), so a plain hash of
    /// one is read back by trying them all; a keyed one is not, without the key. Omitted, records are not
    /// named at all — the log still says what kind of entity was reached and how.
    /// </summary>
    public string? HmacKey { get; set; }

    /// <summary>Which key <see cref="HmacKey"/> is, logged beside each name so a rotated key's names stay readable.</summary>
    public string HmacKeyId { get; set; } = "k1";
}

/// <summary>
/// What one call did, as the interaction log keeps it: who asked for whom, the shape of the question, how it
/// ended, and which records it reached — named by keyed digest. It carries no value, no record key and no
/// label: what a record holds is the source's to show, under the source's rules, and its own audit is the
/// record of it (saem D-83). An operator reading this log learns who looked where and what stopped them,
/// not what they saw.
/// </summary>
internal sealed record Interaction(
    string Id,
    DateTimeOffset At,
    double DurationMs,
    string Subject,
    string? Actor,
    string StartType,
    IReadOnlyList<string> Path,
    IReadOnlyList<string> Read,
    string Outcome,
    IReadOnlyList<AccessedRecord> Accessed,
    InteractionRefusal? Refusal,
    string? ErrorType,
    string? KeyId);

/// <summary>A record the call reached: its system and type, its key as a keyed digest, and what came of it.</summary>
/// <param name="Status"><c>read</c> — returned to the person; <c>notVisible</c> — the source returned nothing to them.</param>
internal sealed record AccessedRecord(string System, string Type, string? Key, string Status);

/// <summary>Where a refused call stopped — the same facts the refusal itself states.</summary>
internal sealed record InteractionRefusal(string Reason, int AtHop, string? Relation, string System);

/// <summary>Writes one interaction per call — to the log, and as attributes of the call's span.</summary>
internal sealed partial class InteractionLog
{
    /// <summary>The source of the spans saem's tool calls are traced under.</summary>
    public static readonly ActivitySource Source = new("Saem.Mcp");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly byte[]? _key;
    private readonly string _keyId;
    private readonly ILogger<InteractionLog> _logger;

    public InteractionLog(IOptions<AuditOptions> options, ILogger<InteractionLog> logger)
    {
        _key = string.IsNullOrEmpty(options.Value.HmacKey) ? null : Encoding.UTF8.GetBytes(options.Value.HmacKey);
        _keyId = options.Value.HmacKeyId;
        _logger = logger;
    }

    /// <summary>
    /// The keyed digest a record is named by: the system, type and key, each length-prefixed so no two
    /// triples run together into the same input. Null when the deployment set no key.
    /// </summary>
    public string? Name(string system, string type, string key)
    {
        if (_key is null)
        {
            return null;
        }

        var input = new StringBuilder();
        foreach (var part in new[] { system, type, key })
        {
            input.Append(part.Length).Append(':').Append(part);
        }

        return Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(input.ToString())));
    }

    /// <summary>The key id to log beside the names, or null when records are not named.</summary>
    public string? KeyId => _key is null ? null : _keyId;

    /// <summary>
    /// The records an answer reached, from the answer itself: every entity a claim or a hop names was read as
    /// the person and returned. Systems come from the ontology, which places each type on one.
    /// </summary>
    public IReadOnlyList<AccessedRecord> Reached(LookupAnswer answer, Func<string, string> systemOf)
    {
        if (answer is not Answered answered)
        {
            return [];
        }

        var seen = new HashSet<(string, string)>();
        var reached = new List<AccessedRecord>();
        void Add(EntityKey entity)
        {
            if (seen.Add((entity.Type, entity.Key)))
            {
                var system = systemOf(entity.Type);
                reached.Add(new AccessedRecord(system, entity.Type, Name(system, entity.Type, entity.Key), "read"));
            }
        }

        foreach (var claim in answered.Claims)
        {
            foreach (var hop in claim.Path)
            {
                Add(hop.From);
                Add(hop.To);
            }

            Add(claim.Entity);
        }

        return reached;
    }

    public void Write(Interaction interaction, Activity? span)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            var serialized = JsonSerializer.Serialize(interaction, Json);
            LogInteraction(interaction.Id, interaction.Outcome, serialized);
        }
        if (span is null)
        {
            return;
        }

        // The span says what kind of call it was and how it ended — never who (a subject identifies a person)
        // or which records: a tracing backend is read by more people than the interaction log.
        span.SetTag("saem.interaction.id", interaction.Id);
        span.SetTag("saem.outcome", interaction.Outcome);
        span.SetTag("saem.start_type", interaction.StartType);
        span.SetTag("saem.hops", interaction.Path.Count);
        span.SetTag("saem.accessed", interaction.Accessed.Count);
        if (interaction.Refusal is { } refusal)
        {
            span.SetTag("saem.refusal.reason", refusal.Reason);
            span.SetTag("saem.refusal.at_hop", refusal.AtHop);
        }

        if (interaction.ErrorType is { } error)
        {
            span.SetTag("error.type", error);
            span.SetStatus(ActivityStatusCode.Error);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "interaction {InteractionId} {Outcome} {Interaction}")]
    private partial void LogInteraction(string interactionId, string outcome, string interaction);
}
