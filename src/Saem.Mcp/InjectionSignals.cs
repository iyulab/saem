using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Saem.Contracts;

namespace Saem.Mcp;

/// <summary>
/// Marks claim values that look like instructions to the model reading them rather than facts. A value is
/// whatever a person typed into a source system — a note, a description — and an agent that reads it as
/// part of its context can be steered by it. saem does not change the value (it is the source's fact, cited
/// as such); it says which values carry the signs, so the consumer can hold them apart.
/// <para>
/// The checks are fixed rules, not a model: no model sits on the answer's path (saem D-72), and a rule says
/// the same thing every time it is asked. They look for what text written for a reader has no reason to
/// contain — characters that do not render, and phrases addressed to an assistant. A value without them is
/// not vouched for: the absence of a signal is not a guarantee.
/// </para>
/// </summary>
internal static partial class InjectionSignals
{
    /// <summary>The <c>_meta</c> key the signals travel under, until the protocol names one.</summary>
    public const string MetaKey = "saem/injectionSuspect";

    /// <summary>The signals a text carries, by name; empty when none.</summary>
    public static IReadOnlyList<string> Of(string text)
    {
        var signals = new List<string>();
        if (text.Any(Invisible))
        {
            signals.Add("invisibleCharacters");
        }

        if (Addressed().IsMatch(text))
        {
            signals.Add("instructionPhrase");
        }

        if (Markup().IsMatch(text))
        {
            signals.Add("activeMarkup");
        }

        return signals;
    }

    /// <summary>
    /// The signals of every claim whose value is text, as <c>[{ claim, signals }]</c> by claim index; null
    /// when no claim carries any — so a clean answer carries no <c>_meta</c> entry at all.
    /// </summary>
    public static JsonArray? For(LookupAnswer answer)
    {
        if (answer is not Answered answered)
        {
            return null;
        }

        var marked = new JsonArray();
        for (var i = 0; i < answered.Claims.Count; i++)
        {
            if (answered.Claims[i].Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var signals = Of(answered.Claims[i].Value.GetString()!);
            if (signals.Count > 0)
            {
                marked.Add(new JsonObject
                {
                    ["claim"] = i,
                    ["signals"] = new JsonArray([.. signals.Select(s => (JsonNode)s)]),
                });
            }
        }

        return marked.Count > 0 ? marked : null;
    }

    // Zero-width and joiner characters, bidirectional overrides and isolates, the byte-order mark used mid-text,
    // and the tag block — all render as nothing, which is the point of hiding an instruction in them.
    private static bool Invisible(char c) =>
        c is >= '​' and <= '‏'
        or >= '‪' and <= '‮'
        or >= '⁠' and <= '⁤'
        or >= '⁦' and <= '⁩'
        or '﻿'
        // The tag block (U+E0000–U+E007F) is reached through its high surrogate.
        or '\uDB40';

    [GeneratedRegex(@"\b(ignore|disregard|forget)\s+(all\s+|any\s+|the\s+)?(previous|prior|above|earlier)\s+(instructions|prompts?|rules)\b|\b(system\s+prompt|you\s+are\s+now|new\s+instructions\s*:|as\s+an?\s+(ai|assistant|language\s+model))\b|(이전|위의?)\s*(지시|명령|지침)(을|를)?\s*(무시|잊)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Addressed();

    [GeneratedRegex(@"<\s*(script|iframe|img|object|embed)\b|\]\(\s*(javascript|data):|!\[[^\]]*\]\(\s*https?://", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Markup();
}
