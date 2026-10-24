using System.Text.Json.Nodes;

namespace Saem.Mcp;

/// <summary>
/// An interaction as an OCSF 1.9.0 event, for a SIEM that reads that schema: <c>API Activity</c> (class 6003, category
/// 6), activity <c>Read</c> — a lookup only ever reads. The person the call was for is <c>actor.user.uid</c>; the agent
/// that made it on their behalf, when the token says one did (<c>act</c>), is <c>ai_agent.uid</c> under the
/// <c>ai_operation</c> profile. The records reached are <c>resources</c>, named by the same keyed digests as the
/// interaction log, never by key. What OCSF has no attribute for — the path, the fields read, where a refusal stopped,
/// which key named the records — goes under <c>unmapped</c>. Like the interaction it is made from, an event holds no
/// value, record key or label.
/// </summary>
internal static class OcsfEvents
{
    private const int ApiActivity = 6003;
    private const int ApplicationActivity = 6;
    private const int Read = 2;
    private const int Informational = 1;

    /// <summary>The event for <paramref name="interaction"/>.</summary>
    public static JsonObject ApiActivityOf(Interaction interaction)
    {
        ArgumentNullException.ThrowIfNull(interaction);

        // A refusal is an answer — the call did what it was asked; only a malformed or failed call is a failure.
        var succeeded = interaction.Outcome is "answered" or "refused" or "candidates";
        var unmapped = new JsonObject
        {
            ["saem_start_type"] = interaction.StartType,
            ["saem_path"] = new JsonArray([.. interaction.Path.Select(p => (JsonNode)p)]),
            ["saem_read"] = new JsonArray([.. interaction.Read.Select(f => (JsonNode)f)]),
            ["saem_duration_ms"] = interaction.DurationMs,
        };
        if (interaction.KeyId is { } keyId)
        {
            unmapped["saem_record_key_id"] = keyId;
        }

        if (interaction.Refusal is { } refusal)
        {
            unmapped["saem_refusal_at_hop"] = refusal.AtHop;
            unmapped["saem_refusal_system"] = refusal.System;
            if (refusal.Relation is { } relation)
            {
                unmapped["saem_refusal_relation"] = relation;
            }
        }

        if (interaction.SuspectClaims > 0)
        {
            unmapped["saem_injection_suspect_claims"] = interaction.SuspectClaims;
        }

        var metadata = new JsonObject
        {
            ["version"] = "1.9.0",
            ["uid"] = interaction.Id,
            ["correlation_uid"] = interaction.Id,
            ["product"] = new JsonObject { ["name"] = "saem" },
        };
        var activity = new JsonObject
        {
            ["class_uid"] = ApiActivity,
            ["category_uid"] = ApplicationActivity,
            ["activity_id"] = Read,
            ["type_uid"] = (ApiActivity * 100) + Read,
            ["severity_id"] = Informational,
            ["time"] = interaction.At.ToUnixTimeMilliseconds(),
            ["status_id"] = succeeded ? 1 : 2,
            ["status"] = interaction.Outcome,
            ["metadata"] = metadata,
            ["actor"] = new JsonObject { ["user"] = new JsonObject { ["uid"] = interaction.Subject } },
            ["api"] = new JsonObject { ["operation"] = "lookup" },
            // The call reaches saem over MCP; the network address of the caller is the host's to log, not saem's.
            ["src_endpoint"] = new JsonObject { ["svc_name"] = "mcp" },
            ["resources"] = new JsonArray([.. interaction.Accessed.Select(r => (JsonNode)Resource(r))]),
            ["unmapped"] = unmapped,
        };
        if (interaction.Refusal is { } refused)
        {
            activity["status_detail"] = refused.Reason;
        }
        else if (interaction.ErrorType is { } error)
        {
            activity["status_detail"] = error;
        }

        if (interaction.Actor is { } agent)
        {
            metadata["profiles"] = new JsonArray("ai_operation");
            activity["ai_agent"] = new JsonObject { ["uid"] = agent };
        }

        return activity;
    }

    private static JsonObject Resource(AccessedRecord record)
    {
        var resource = new JsonObject
        {
            ["type"] = record.Type,
            ["data"] = new JsonObject { ["system"] = record.System, ["status"] = record.Status },
        };
        if (record.Key is { } digest)
        {
            resource["uid"] = digest;
        }

        return resource;
    }
}
