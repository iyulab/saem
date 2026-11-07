---
name: saem-lookup
description: How to ask saem's `lookup` tool for facts about an organisation's records, and how to read what comes back - an answer with its sources, or a refusal that never means "it does not exist". Use when an agent has the saem MCP server and must answer a question about people, items, orders or other declared entities.
---

# Asking saem

saem has one tool, `lookup`. It reads facts from the organisation's systems **as the person the request acts for**; each
source decides what that person may see. The tool's input schema is generated from the declared ontology, so the entity
types, relations and fields you may name are the ones the schema lists - **name only those**.

## Shape of a lookup

- `start` - a declared type and which entity: its key, or a declared field's value (the schema says which forms are allowed).
- `path` - the relations to follow, in order; empty when you read the entity you started at.
- `read` - the declared fields of the entity the walk **ends at**.

"What is the name of the item on work order WO-2401?" is: start the work-order type at `WO-2401`, follow the relation to the
item, read the item's name field. One lookup answers it; do not walk step by step with several.

## Read the answer

- `kind: answered` - facts, each a claim (`entity`, `field`, `value`) with `sources` (the record each value was read from) and the
  `path` of relations followed. **Cite sources for every value you state, and cite the sources of the hops too** (`path[].source`):
  the record that named the next entity is part of why the answer is what it is.
- `kind: refused` - the walk found nothing visible to this person, and says where. **A refusal never means the thing does not
  exist**: it may be missing or merely not visible to this person, and saem cannot tell which. Report exactly that; never
  convert a refusal into "there is no such record", and never fill the gap from memory. When the records were filed only a
  moment ago, a source that projects them (a form store, say) may not have brought them forward yet: if you can reach that
  source's own status, check it before reporting the refusal, and say so when you cannot.
- An error that says a type, relation or field is not declared means the ontology does not cover the question. Say it is outside
  what saem knows; do not rephrase until something matches by accident.
- Ask something the declared data cannot answer (a price nobody filed, a supplier nobody linked) and the honest answer is "this
  cannot be answered from what I can look up", not a guess.

## Limits you will meet

A step that would spread too wide, or a lookup that takes too long, is refused as such rather than cut short - narrow the start
or the path. You cannot widen your own access: the same lookup asked by someone else may answer differently.

## For whoever configures the agent

The token the agent presents decides everything: its `sub` is the person, an `act` claim names the agent acting for them, and
`groups` choose which materialized partitions are read. Give the agent the **person's** delegated token, not a service credential.
See the MCP server section of the README.
