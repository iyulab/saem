using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Saem.Contracts;
using Saem.Ontology;
using Saem.Query;

namespace Saem.Materialization;

/// <summary>
/// The entities an identity found to be one thing, by their IRIs in ordinal order: at least two, and — for an
/// identity between two types — at least one of each, since a pair the identity states is one of each.
/// </summary>
/// <param name="Identity">The identity, as the ontology names it.</param>
/// <param name="Members">The members' IRIs, in ordinal order.</param>
public sealed record IdentityCluster(string Identity, IReadOnlyList<string> Members)
{
    /// <summary>Equal when of one identity with the same members in the same order.</summary>
    public bool Equals(IdentityCluster? other) => other is not null && Identity == other.Identity && Members.SequenceEqual(other.Members);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Identity, Members.Count);
}

/// <summary>
/// Finds what each identity that declares a field match says is one thing, from what the sessions let their holder
/// read. It reads the whole table of each of the identity's types (<see cref="ISourceSession.ScanAsync"/>), with the
/// matched columns only, and puts together the entities whose matched fields hold the same values — every field of
/// the match, none of them empty. Equal values are equal however many rows hold them, so the entities sharing one set
/// of values are one cluster, and two clusters never share an entity: no closure is taken over anything a person or a
/// rule did not state.
/// <para>
/// The sessions are the group's, as for its edges: a row the group may not see is not read, so the clusters a group
/// gets are the ones its own account reaches. An identity by a linkage rule is not matched here — its pairs are scored,
/// not equal.
/// </para>
/// </summary>
public sealed class IdentityMatcher(Ontology.Ontology ontology, EntityIris iris)
{
    /// <summary>The clusters of every field-matched identity, identity by identity in name order, each's in order of its first member.</summary>
    public async IAsyncEnumerable<IdentityCluster> MatchAsync(
        ISourceSessions sessions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        var open = new Dictionary<string, ISourceSession>(StringComparer.Ordinal);
        try
        {
            foreach (var identity in ontology.Identities.Values.Where(i => i.Match is not null).OrderBy(i => i.Name, StringComparer.Ordinal))
            {
                var first = ontology.EntityTypes[identity.First];
                var second = ontology.EntityTypes[identity.Second];
                var within = ReferenceEquals(first, second);

                // Entities by the values their matched fields hold, each type's apart: a cluster is of the two types.
                var byValues = new Dictionary<string, (SortedSet<string> First, SortedSet<string> Second)>(StringComparer.Ordinal);
                var sides = new List<(EntityTypeDeclaration Type, List<string> Fields)> { (first, [.. identity.Match!.Select(p => p.First)]) };
                if (!within)
                {
                    sides.Add((second, [.. identity.Match!.Select(p => p.Second)]));
                }

                for (var side = 0; side < sides.Count; side++)
                {
                    var (type, fields) = sides[side];
                    var session = await OpenAsync(open, sessions, type.System, cancellationToken).ConfigureAwait(false);
                    var columns = fields.Select(f => type.Fields[f]).Distinct(StringComparer.Ordinal).ToList();
                    await foreach (var row in session.ScanAsync(type.Table, type.Key, columns, cancellationToken).ConfigureAwait(false))
                    {
                        if (ValuesOf(type, fields, row) is not { } values)
                        {
                            continue;
                        }

                        if (!byValues.TryGetValue(values, out var entities))
                        {
                            entities = (new SortedSet<string>(StringComparer.Ordinal), new SortedSet<string>(StringComparer.Ordinal));
                            byValues.Add(values, entities);
                        }

                        (side == 0 ? entities.First : entities.Second).Add(iris.For(type.Name, type.Normalize(row.Key)));
                    }
                }

                var clusters = byValues.Values
                    .Where(e => within ? e.First.Count > 1 : e.First.Count > 0 && e.Second.Count > 0)
                    .Select(e => new IdentityCluster(identity.Name, [.. e.First.Concat(e.Second).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]))
                    .OrderBy(c => c.Members[0], StringComparer.Ordinal);
                foreach (var cluster in clusters)
                {
                    yield return cluster;
                }
            }
        }
        finally
        {
            foreach (var session in open.Values)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask<ISourceSession> OpenAsync(
        Dictionary<string, ISourceSession> open, ISourceSessions sessions, string system, CancellationToken cancellationToken)
    {
        if (!open.TryGetValue(system, out var session))
        {
            session = await sessions.OpenAsync(system, cancellationToken).ConfigureAwait(false);
            open.Add(system, session);
        }

        return session;
    }

    /// <summary>
    /// The row's matched values as one text — each trimmed, a number field's spelled as the integer it is — or null
    /// when one is empty: an entity that holds no value for a field shares it with no one.
    /// </summary>
    private static string? ValuesOf(EntityTypeDeclaration type, List<string> fields, SourceRow row)
    {
        var values = new List<string>(fields.Count);
        foreach (var field in fields)
        {
            if (Text(row.Values[type.Fields[field]])?.Trim() is not { Length: > 0 } value)
            {
                return null;
            }

            var kind = type.Kinds is { } kinds && kinds.TryGetValue(field, out var k) ? k : (FieldKind?)null;
            values.Add(kind == FieldKind.Number && BigInteger.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
                ? number.ToString(CultureInfo.InvariantCulture)
                : value);
        }

        // Each value led by its length, so no value can be read as the end of another.
        return string.Concat(values.Select(v => string.Create(CultureInfo.InvariantCulture, $"{v.Length}:{v}")));
    }

    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        _ => value.GetRawText(),
    };
}
