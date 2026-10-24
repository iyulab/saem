using System.Text.Json;

namespace Saem.Contracts;

/// <summary>
/// The answer contract as a JSON Schema (draft 2020-12): what <see cref="ContractJson"/> writes for a
/// <see cref="LookupAnswer"/>. The rules the constructors hold — a claim names at least one source, an
/// answer states at least one claim, candidates are two or more — are held here too, so a consumer that
/// validates against the schema refuses what the contract cannot build. saem declares it as its tool's
/// output schema.
/// </summary>
public static class ContractSchema
{
    /// <summary>The schema of a lookup's answer.</summary>
    public static JsonElement Answer { get; } = JsonDocument.Parse(AnswerText).RootElement.Clone();

    private const string AnswerText = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "title": "saem lookup answer",
          "type": "object",
          "required": ["kind"],
          "properties": {
            "kind": { "enum": ["answered", "refused", "candidates"] },
            "answeredFor": { "$ref": "#/$defs/viewer" },
            "catalogVersion": { "type": "string" },
            "partitions": { "type": "array", "items": { "$ref": "#/$defs/partitionRead" } }
          },
          "oneOf": [
            {
              "properties": {
                "kind": { "const": "answered" },
                "claims": { "type": "array", "minItems": 1, "items": { "$ref": "#/$defs/claim" } }
              },
              "required": ["kind", "claims"]
            },
            {
              "properties": {
                "kind": { "const": "refused" },
                "refusal": { "$ref": "#/$defs/refusal" }
              },
              "required": ["kind", "refusal"]
            },
            {
              "properties": {
                "kind": { "const": "candidates" },
                "entities": { "type": "array", "minItems": 2, "items": { "$ref": "#/$defs/candidate" } }
              },
              "required": ["kind", "entities"]
            }
          ],
          "$defs": {
            "entityKey": {
              "type": "object",
              "required": ["type", "key"],
              "properties": { "type": { "type": "string" }, "key": { "type": "string" } }
            },
            "viewer": {
              "type": "object",
              "required": ["subject"],
              "properties": { "subject": { "type": "string" }, "actedBy": { "type": "string" } }
            },
            "sourceRecord": {
              "type": "object",
              "required": ["system", "table", "record"],
              "properties": {
                "system": { "type": "string" },
                "table": { "type": "string" },
                "record": { "type": "string" },
                "readAt": { "type": "string", "format": "date-time" },
                "projection": { "$ref": "#/$defs/sourceProjection" }
              }
            },
            "hop": {
              "type": "object",
              "required": ["relation", "from", "to"],
              "properties": {
                "relation": { "type": "string" },
                "from": { "$ref": "#/$defs/entityKey" },
                "to": { "$ref": "#/$defs/entityKey" },
                "partition": { "type": "string" },
                "partitionAsOf": { "type": "string", "format": "date-time" }
              }
            },
            "claim": {
              "type": "object",
              "required": ["entity", "field", "value", "sources", "path"],
              "properties": {
                "entity": { "$ref": "#/$defs/entityKey" },
                "field": { "type": "string" },
                "value": {},
                "sources": { "type": "array", "minItems": 1, "items": { "$ref": "#/$defs/sourceRecord" } },
                "path": { "type": "array", "items": { "$ref": "#/$defs/hop" } },
                "fieldKind": { "enum": ["text", "freeText", "number", "date", "key"] }
              }
            },
            "refusal": {
              "type": "object",
              "required": ["reason", "atHop", "system", "statement"],
              "properties": {
                "reason": { "enum": ["noVisibleResult", "noMaterializedPartition"] },
                "atHop": { "type": "integer", "minimum": 0 },
                "relation": { "type": ["string", "null"] },
                "system": { "type": "string" },
                "statement": { "type": "string" },
                "projection": { "$ref": "#/$defs/sourceProjection" }
              }
            },
            "candidate": {
              "type": "object",
              "required": ["entity", "source"],
              "properties": {
                "entity": { "$ref": "#/$defs/entityKey" },
                "source": { "$ref": "#/$defs/sourceRecord" }
              }
            },
            "sourceProjection": { "enum": ["notProjected", "stale", "unverified"] },
            "partitionRead": {
              "type": "object",
              "required": ["name", "blocked", "incomplete", "statements"],
              "properties": {
                "name": { "type": "string" },
                "asOf": { "type": ["string", "null"], "format": "date-time" },
                "blocked": { "type": "boolean" },
                "incomplete": { "type": "boolean" },
                "statements": { "type": "array", "items": { "type": "string" } }
              }
            }
          }
        }
        """;
}
