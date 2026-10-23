using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Saem.Contracts;

namespace Saem.Materialization;

/// <summary>
/// Keeps a group's instance edges in that group's own Formbase, over its HTTP surface: one form type,
/// <see cref="FormType"/>, whose declaration has the edge's six text fields and nothing else — there is no
/// field a record's body could be written into, so the store cannot come to hold one.
/// <para>
/// A pass replaces what the store holds with the edges it is given. Each edge is the record keyed by its
/// relation and the entity it leaves (a row leads through a relation to one entity at most): an edge that is
/// already held unchanged is not sent again, a changed one is sent as a correction of its record, and a held
/// edge the pass did not give is retired. The documents stay in the stream, as Formbase keeps every
/// correction — the projection shows the edges of the latest pass.
/// </para>
/// <para>
/// Which group the store is is the host's: the client reaches a Formbase started with that group's project
/// and the secret confined to it, so a write meant for another group has no way there.
/// </para>
/// </summary>
/// <param name="http">A client whose <see cref="HttpClient.BaseAddress"/> is the group's Formbase. The caller owns its lifetime.</param>
/// <param name="formbaseNamespace">The Formbase namespace to address, sent as <c>Formbase-Namespace</c>; omitted, the instance's own.</param>
public sealed class FormbaseEdgeStore(HttpClient http, string? formbaseNamespace = null)
{
    /// <summary>The form type the edges are filed under.</summary>
    public const string FormType = "edges";

    /// <summary>The table the edges project into.</summary>
    public const string TableName = "saem_edges";

    private const string NamespaceHeader = "Formbase-Namespace";
    private const int PageSize = 1000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly string[] Fields = ["from_iri", "relation", "to_iri", "source_system", "source_table", "source_record"];

    /// <summary>Makes the store hold exactly <paramref name="edges"/>, and projects them.</summary>
    public async Task<EdgeWrite> ReplaceAsync(IAsyncEnumerable<MaterializedEdge> edges, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edges);

        await DeclareAsync(cancellationToken).ConfigureAwait(false);
        var held = await HeldAsync("", cancellationToken).ConfigureAwait(false);

        var given = new HashSet<string>(StringComparer.Ordinal);
        int written = 0, unchanged = 0;
        await foreach (var edge in edges.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var key = KeyOf(edge);
            if (!given.Add(key))
            {
                throw new EdgeStoreException($"Two edges of relation '{edge.Relation}' leave '{edge.From}'; an entity leads through a relation to one entity at most.");
            }

            var fields = FieldsOf(edge);
            if (held.TryGetValue(key, out var current) && current.SequenceEqual(fields))
            {
                unchanged++;
                continue;
            }

            using var request = Request(HttpMethod.Post, $"formtypes/{FormType}/documents?recordKey={Uri.EscapeDataString(key)}");
            request.Content = JsonContent.Create(Fields.Zip(fields).ToDictionary(p => p.First, p => p.Second), options: Json);
            await SendAsync(request, "Filing an edge", cancellationToken).ConfigureAwait(false);
            written++;
        }

        var retired = 0;
        foreach (var key in held.Keys.Where(k => !given.Contains(k)).Order(StringComparer.Ordinal))
        {
            using var request = Request(HttpMethod.Delete, $"formtypes/{FormType}/records?recordKey={Uri.EscapeDataString(key)}");
            await SendAsync(request, "Retiring an edge", cancellationToken).ConfigureAwait(false);
            retired++;
        }

        using (var project = Request(HttpMethod.Post, $"formtypes/{FormType}/projection"))
        {
            await SendAsync(project, "Projecting the edges", cancellationToken).ConfigureAwait(false);
        }

        return new EdgeWrite(written, unchanged, retired);
    }

    /// <summary>The edges the store holds now, in key order.</summary>
    public IAsyncEnumerable<MaterializedEdge> ReadAsync(CancellationToken cancellationToken = default) =>
        EdgesAsync("", cancellationToken);

    /// <summary>
    /// The held edges of <paramref name="relation"/> that lead to the entity named <paramref name="to"/> —
    /// the relation walked backwards — in the order of the entities they leave. Asked of the store, so only
    /// the matching edges are read.
    /// </summary>
    public IAsyncEnumerable<MaterializedEdge> LeadingToAsync(string relation, string to, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(relation);
        ArgumentException.ThrowIfNullOrEmpty(to);
        return EdgesAsync($"&filter={Uri.EscapeDataString("relation:" + relation)}&filter={Uri.EscapeDataString("to_iri:" + to)}", cancellationToken);
    }

    private async IAsyncEnumerable<MaterializedEdge> EdgesAsync(string filter, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var (_, fields) in (await HeldAsync(filter, cancellationToken).ConfigureAwait(false)).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            yield return new MaterializedEdge(fields[0], fields[1], fields[2], new SourceRecord(fields[3], fields[4], fields[5]));
        }
    }

    /// <summary>
    /// Puts the edge declaration in force when the form type has none. One already in force must be the
    /// edge declaration: a store whose form type was declared otherwise is not written over.
    /// </summary>
    private async Task DeclareAsync(CancellationToken cancellationToken)
    {
        using (var read = Request(HttpMethod.Get, $"formtypes/{FormType}/declaration"))
        using (var response = await http.SendAsync(read, cancellationToken).ConfigureAwait(false))
        {
            if (response.IsSuccessStatusCode)
            {
                var declaration = await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken).ConfigureAwait(false);
                var declared = declaration.GetProperty("fields").EnumerateArray()
                    .Select(f => (Name: f.GetProperty("name").GetString(), Type: f.GetProperty("type").GetString(), Nullable: f.GetProperty("nullable").GetBoolean()))
                    .ToList();
                if (declaration.GetProperty("tableName").GetString() != TableName
                    || !declared.SequenceEqual(Fields.Select(f => ((string?)f, (string?)"text", false))))
                {
                    throw new EdgeStoreException($"The form type '{FormType}' is declared with another shape than the edges'; it is not written over.");
                }

                return;
            }

            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                throw await FailureAsync("Reading the edge declaration", response, cancellationToken).ConfigureAwait(false);
            }
        }

        using var put = Request(HttpMethod.Put, $"formtypes/{FormType}/declaration");
        put.Content = JsonContent.Create(new
        {
            tableName = TableName,
            declarationVersion = 1,
            fields = Fields.Select(name => new { name, type = "text", nullable = false }),
        }, options: Json);
        await SendAsync(put, "Declaring the edges", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The projected edges <paramref name="filter"/> admits (query parameters, each led by <c>&amp;</c>), by
    /// record key; none before the first pass declared and projected them.
    /// </summary>
    private async Task<Dictionary<string, string[]>> HeldAsync(string filter, CancellationToken cancellationToken)
    {
        var held = new Dictionary<string, string[]>(StringComparer.Ordinal);
        for (var offset = 0; ; offset += PageSize)
        {
            using var request = Request(HttpMethod.Get, $"formtypes/{FormType}/records?limit={PageSize}&offset={offset}{filter}");
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound
                && await ProblemAsync(response, cancellationToken).ConfigureAwait(false) is "/problems/not-projected" or "/problems/no-declaration")
            {
                return held;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw await FailureAsync("Reading the held edges", response, cancellationToken).ConfigureAwait(false);
            }

            var page = await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken).ConfigureAwait(false);
            // A Formbase before 0.35 does not report skips; one that does and reports some cannot be read in full.
            if (page.TryGetProperty("skippedDocuments", out var skipped) && skipped.GetInt32() > 0)
            {
                throw new EdgeStoreException("The edge projection skipped documents, so the held edges cannot be read in full.");
            }

            var rows = page.GetProperty("rows");
            foreach (var row in rows.EnumerateArray())
            {
                var key = row.GetProperty("record").GetProperty("key").GetString()
                    ?? throw new EdgeStoreException("An edge record without a key is in the store; saem files every edge under one.");
                var fields = row.GetProperty("fields");
                held[key] = [.. Fields.Select(f => fields.GetProperty(f).GetString() ?? "")];
            }

            if (rows.GetArrayLength() < PageSize)
            {
                return held;
            }
        }
    }

    private static string KeyOf(MaterializedEdge edge) => edge.Relation + " " + edge.From;

    private static string[] FieldsOf(MaterializedEdge edge) =>
        [edge.From, edge.Relation, edge.To, edge.Source.System, edge.Source.Table, edge.Source.Record];

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        if (formbaseNamespace is not null)
        {
            request.Headers.Add(NamespaceHeader, formbaseNamespace);
        }

        return request;
    }

    private async Task SendAsync(HttpRequestMessage request, string what, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await FailureAsync(what, response, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string?> ProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken).ConfigureAwait(false);
            return problem.TryGetProperty("type", out var type) ? type.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<EdgeStoreException> FailureAsync(string what, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new EdgeStoreException($"{what} answered {(int)response.StatusCode}: {body}");
    }
}

/// <summary>What a pass did to the store.</summary>
/// <param name="Written">Edges sent — new ones, and held ones whose entity now leads elsewhere or is cited from another record.</param>
/// <param name="Unchanged">Edges the store already held as given, not sent again.</param>
/// <param name="Retired">Held edges the pass did not give, taken out.</param>
public sealed record EdgeWrite(int Written, int Unchanged, int Retired);

/// <summary>The group's edge store refused a pass, or holds what saem did not write.</summary>
public sealed class EdgeStoreException : Exception
{
    public EdgeStoreException()
    {
    }

    public EdgeStoreException(string message)
        : base(message)
    {
    }

    public EdgeStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
