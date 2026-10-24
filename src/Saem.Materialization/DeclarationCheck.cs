using System.Numerics;
using System.Globalization;
using System.Text.Json;
using Saem.Ontology;
using Saem.Query;

namespace Saem.Materialization;

/// <summary>
/// Checks the declarations against the sources, as whoever holds the sessions — a builder's own account. The
/// static checks (<see cref="Ontology.Ontology.Create"/>) know names; only the sources know whether a declared table
/// and its columns are there, whether a key names one row, whether a number key spells an integer, and how many
/// of a relation's values name an entity of the type it leads to. Each type's table is read once, whole
/// (<see cref="ISourceSession.ScanAsync"/>), so the check works on every source saem reads, and sees exactly what
/// the account sees: its findings hold for the rows the account may read, and say so.
/// <para>
/// A finding cites the records it is about by their key, up to <see cref="SampleSize"/> of them. Keys are values of
/// the sources, so the report is kept as the interaction log is — by whoever runs the deployment — and never goes
/// into an answer.
/// </para>
/// </summary>
public sealed class DeclarationCheck(Ontology.Ontology ontology)
{
    /// <summary>How many records a finding cites at most.</summary>
    public const int SampleSize = 5;

    /// <summary>Reads every declared table through <paramref name="sessions"/> and reports what does not hold.</summary>
    public async Task<CheckReport> RunAsync(ISourceSessions sessions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        var findings = new List<CheckFinding>();
        var keys = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var viaValues = new Dictionary<string, List<(string Key, string Value)>>(StringComparer.Ordinal);
        var open = new Dictionary<string, ISourceSession>(StringComparer.Ordinal);
        try
        {
            foreach (var type in ontology.EntityTypes.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                var leaving = ontology.Relations.Values.Where(r => r.From == type.Name).OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
                var columns = type.Fields.Values.Concat(type.Record ?? []).Distinct(StringComparer.Ordinal).ToList();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var duplicate = new List<string>();
                var emptyRecord = new List<string>();
                var notInteger = new List<string>();
                int rows = 0, emptyKeys = 0;
                foreach (var relation in leaving)
                {
                    viaValues[relation.Name] = [];
                }

                try
                {
                    if (!open.TryGetValue(type.System, out var session))
                    {
                        session = await sessions.OpenAsync(type.System, cancellationToken).ConfigureAwait(false);
                        open.Add(type.System, session);
                    }

                    await foreach (var row in session.ScanAsync(type.Table, type.Key, columns, cancellationToken).ConfigureAwait(false))
                    {
                        rows++;
                        if (string.IsNullOrEmpty(row.Key))
                        {
                            emptyKeys++;
                            continue;
                        }

                        if (type.KeyKind == KeyKind.Number && !BigInteger.TryParse(row.Key.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                        {
                            notInteger.Add(row.Key);
                        }

                        if (!seen.Add(type.Normalize(row.Key)))
                        {
                            duplicate.Add(row.Key);
                        }

                        if (type.Record is { } record && record.Any(c => string.IsNullOrEmpty(Text(row.Values[c]))))
                        {
                            emptyRecord.Add(row.Key);
                        }

                        foreach (var relation in leaving)
                        {
                            if (Text(row.Values[type.Fields[relation.Via]]) is { Length: > 0 } value)
                            {
                                viaValues[relation.Name].Add((row.Key, value));
                            }
                        }
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // A table or column the declarations name and the source does not answer for.
                    findings.Add(new CheckFinding(type.Name, null, "unreadable", $"'{type.System}'.'{type.Table}' could not be read: {e.Message.ReplaceLineEndings(" ").Trim()}", 0, []));

                    // A session that failed a read may be unusable after it (a transaction the source aborted), so the
                    // system's next table is read over a fresh one rather than reported unreadable for this one's fault.
                    if (open.Remove(type.System, out var failed))
                    {
                        await failed.DisposeAsync().ConfigureAwait(false);
                    }

                    foreach (var relation in leaving)
                    {
                        // Its relations' values were not read, so there is nothing to resolve.
                        viaValues.Remove(relation.Name);
                    }

                    continue;
                }

                keys[type.Name] = seen;
                AddIf(findings, type.Name, "duplicateKey", $"rows share a key of '{type.Key}' — a lookup by such a key is a failure, not an answer", duplicate);
                if (emptyKeys > 0)
                {
                    findings.Add(new CheckFinding(type.Name, null, "emptyKey", $"rows have no '{type.Key}' — no lookup can name them", emptyKeys, []));
                }

                AddIf(findings, type.Name, "emptyRecord", $"rows leave a record column ({string.Join(", ", type.Record ?? [])}) empty — an answer from them cites no record", emptyRecord);
                AddIf(findings, type.Name, "notInteger", $"keys of '{type.Key}' spell no integer, though the type declares keyKind number", notInteger);
                findings.Add(new CheckFinding(type.Name, null, "read", $"{rows} row(s) read", rows, []));
            }

            foreach (var relation in ontology.Relations.Values.OrderBy(r => r.Name, StringComparer.Ordinal))
            {
                if (!keys.TryGetValue(relation.To, out var targets) || !viaValues.TryGetValue(relation.Name, out var values))
                {
                    continue;
                }

                var to = ontology.EntityTypes[relation.To];
                var unresolved = values.Where(v => !targets.Contains(to.Normalize(v.Value))).Select(v => v.Key).ToList();
                findings.Add(new CheckFinding(relation.From, relation.Name, unresolved.Count > 0 ? "unresolved" : "resolved",
                    $"{values.Count - unresolved.Count} of {values.Count} value(s) of '{relation.Via}' name a '{relation.To}' the account can read",
                    unresolved.Count, [.. unresolved.Take(SampleSize)]));
            }
        }
        finally
        {
            foreach (var session in open.Values)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }

        return new CheckReport(findings);
    }

    private static void AddIf(List<CheckFinding> findings, string type, string kind, string what, List<string> records)
    {
        if (records.Count > 0)
        {
            findings.Add(new CheckFinding(type, null, kind, what, records.Count, [.. records.Take(SampleSize)]));
        }
    }

    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        _ => value.GetRawText(),
    };
}

/// <summary>What a check of the declarations against the sources found.</summary>
/// <param name="Findings">Every finding, the counts included; <see cref="Violations"/> are the ones that do not hold.</param>
public sealed record CheckReport(IReadOnlyList<CheckFinding> Findings)
{
    /// <summary>The findings that say a declaration does not hold for the rows the account reads.</summary>
    public IReadOnlyList<CheckFinding> Violations => [.. Findings.Where(f => f.IsViolation)];

    /// <summary>
    /// What changed from <paramref name="before"/> — the same sources checked against the declarations a change replaces —
    /// to this report: each finding whose count differs, or that one of the two has and the other has not, by type,
    /// relation and kind. The change a catalog makes, simulated against the sources before it is put in force.
    /// </summary>
    public IReadOnlyList<CheckDifference> Against(CheckReport before)
    {
        ArgumentNullException.ThrowIfNull(before);
        static Dictionary<(string, string?, string), int> Counts(CheckReport report) =>
            report.Findings.ToDictionary(f => (f.Type, f.Relation, f.Kind), f => f.Count);

        var was = Counts(before);
        var now = Counts(this);
        return [.. was.Keys.Union(now.Keys)
            .Select(k => new CheckDifference(k.Item1, k.Item2, k.Item3, was.TryGetValue(k, out var b) ? b : null, now.TryGetValue(k, out var a) ? a : null))
            .Where(d => d.Before != d.After)
            .OrderBy(d => d.Type, StringComparer.Ordinal).ThenBy(d => d.Relation, StringComparer.Ordinal).ThenBy(d => d.Kind, StringComparer.Ordinal)];
    }
}

/// <summary>A finding that differs between two checks of the same sources.</summary>
/// <param name="Type">The entity type it is about.</param>
/// <param name="Relation">The relation it is about, or null when it is about the type's own table.</param>
/// <param name="Kind">The finding's kind.</param>
/// <param name="Before">Its count in the check before; null when that check did not find it.</param>
/// <param name="After">Its count in the check after; null when that check does not find it.</param>
public sealed record CheckDifference(string Type, string? Relation, string Kind, int? Before, int? After)
{
    /// <summary>Whether the change makes a declaration hold for fewer rows: a violation found anew, or found of more of them.</summary>
    public bool IsWorse => CheckFinding.IsViolationKind(Kind) && After is { } after && (Before is not { } before || after > before);
}

/// <summary>One finding of a check.</summary>
/// <param name="Type">The entity type it is about.</param>
/// <param name="Relation">The relation it is about, or null when it is about the type's own table.</param>
/// <param name="Kind">What was found: <c>read</c> and <c>resolved</c> are counts; the rest are violations.</param>
/// <param name="What">The finding, in words.</param>
/// <param name="Count">How many rows or values it is about.</param>
/// <param name="Records">Up to <see cref="DeclarationCheck.SampleSize"/> of the records it is about, by key.</param>
public sealed record CheckFinding(string Type, string? Relation, string Kind, string What, int Count, IReadOnlyList<string> Records)
{
    /// <summary>Whether it says a declaration does not hold — every kind but the counts <c>read</c> and <c>resolved</c>.</summary>
    public bool IsViolation => IsViolationKind(Kind);

    internal static bool IsViolationKind(string kind) => kind is not ("read" or "resolved");
}
