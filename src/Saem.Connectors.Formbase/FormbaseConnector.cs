using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Eyu.Core.Declared;
using Eyu.Core.Ports;
using Eyu.Core.Primitives;
using Eyu.Core.Records;

namespace Saem.Connectors.Formbase;

/// <summary>
/// Reads one Formbase instance over its HTTP surface and hands Eyu what it asks for: what a form
/// type declares (<see cref="IStructureSource"/>) and the documents it holds
/// (<see cref="IRecordSample"/>). A subject is a form type.
/// <para>
/// Read-only, and only over HTTP. Formbase runs in its own process and saem never loads its engine:
/// the process boundary is the licence boundary, and a connector that linked the engine in would
/// also be reading Formbase's storage behind its back. Nothing here writes, and nothing here judges —
/// entities and relations are Eyu's to propose from what this returns.
/// </para>
/// <para>
/// Documents are read from the raw stream, not from projected records: a projection carries only the
/// declared columns, and the fields nobody has declared yet are exactly what Eyu is asked to judge.
/// </para>
/// </summary>
/// <param name="http">
/// A client whose <see cref="HttpClient.BaseAddress"/> is the Formbase instance. The caller owns its
/// lifetime and any authentication in front of the instance.
/// </param>
/// <param name="formbaseNamespace">
/// The Formbase namespace to address, sent as <c>Formbase-Namespace</c>; omitted, the instance's own.
/// </param>
public sealed class FormbaseConnector(HttpClient http, string? formbaseNamespace = null) : IStructureSource, IRecordSample
{
    private const string NamespaceHeader = "Formbase-Namespace";
    private const string NoDeclaration = "/problems/no-declaration";

    /// <summary>The largest page the raw stream serves; a sample larger than this is read in several pages.</summary>
    private const int MaxPageSize = 1000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<DeclaredStructure?> GetStructureAsync(SubjectRef subject, CancellationToken cancellationToken = default)
    {
        if (await ReadDeclarationAsync(subject, cancellationToken).ConfigureAwait(false) is not { } declaration)
        {
            return null;
        }

        var fields = (declaration.Fields ?? [])
            .Select(field => new DeclaredField(field.Name, Kind: ToDeclaredKind(field.Type), Required: !field.Nullable))
            .ToList();

        var declaredRelations = (declaration.Relations ?? [])
            .Select(relation => new DeclaredRelation(
                relation.Name,
                SubjectRef.Create(relation.Target),
                ViaField: relation.KeyField,
                Kind: ToRelationKind(relation.Kind)));
        var relations = WithBoundFields(WithParent(declaredRelations, declaration.SectionOf), declaration.Fields ?? []);

        return new DeclaredStructure(subject, fields, relations, declaration.DeclarationVersion.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// For a repeated section, the declared relations and the link back to the parent its rows are cut
    /// from, through the field each row carries the parent's value in — the same link Eyu's in-process
    /// Formbase adapter reads. Without it, two rows of one sheet agree on that field and nothing says why.
    /// </summary>
    private static IEnumerable<DeclaredRelation> WithParent(IEnumerable<DeclaredRelation> declared, SectionOfResponse? section) =>
        section is null
            ? declared
            : [.. declared, new DeclaredRelation(section.Relation, SubjectRef.Create(section.FormType), ViaField: section.KeyField, Kind: DeclaredRelationKind.Parent)];

    /// <summary>
    /// The declaration in force for <paramref name="subject"/>, or null when it has none.
    /// </summary>
    private async Task<DeclarationResponse?> ReadDeclarationAsync(SubjectRef subject, CancellationToken cancellationToken)
    {
        using var response = await SendAsync($"formtypes/{Uri.EscapeDataString(subject.Value)}/declaration", cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // Only the answer "this form type has no declaration" means nothing is declared. Any other
            // 404 — a wrong base address, a host without this route — is a failure to read, and reading
            // it as "nothing declared" would send Eyu off to infer what was in fact declared.
            var problem = await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound && problem?.Type == NoDeclaration)
            {
                return null;
            }

            throw Refused(response, problem);
        }

        return await response.Content.ReadFromJsonAsync<DeclarationResponse>(Json, cancellationToken).ConfigureAwait(false)
            ?? throw new FormbaseConnectorException($"Formbase answered the declaration of '{subject}' with an empty body.");
    }

    /// <summary>
    /// The declared relations, then one for each field bound to another form type (its <c>target</c>): a
    /// bound field is a declared reference too, only declared on the field rather than as a relation, and
    /// leaving it out would drop a declared fact. The bound field itself carries the reference — its value
    /// is the target's — and so does the field holding the target record's key, when the binding names
    /// one (<c>viaField</c>). Each becomes a reference relation named after the field that carries it; a
    /// field already carrying a declared relation to the same form type adds nothing. The same reading
    /// as Eyu's in-process Formbase adapter, so a declaration means one thing whichever way it is read.
    /// </summary>
    private static List<DeclaredRelation> WithBoundFields(IEnumerable<DeclaredRelation> declared, IEnumerable<FieldResponse> fields)
    {
        var relations = declared.ToList();
        foreach (var field in fields)
        {
            if (field.Target is not { } target)
            {
                continue;
            }

            var subject = SubjectRef.Create(target.FormType);
            foreach (var carrier in target.ViaField is { } via && via != field.Name ? new[] { field.Name, via } : [field.Name])
            {
                if (!relations.Any(r => r.Target == subject && r.ViaField == carrier))
                {
                    relations.Add(new DeclaredRelation(carrier, subject, ViaField: carrier, Kind: DeclaredRelationKind.Reference));
                }
            }
        }

        return relations;
    }

    /// <summary>
    /// The form type's records in the order Formbase accepted them, oldest first, up to
    /// <paramref name="maxCount"/>. Each record's top-level members become its fields — a string as its
    /// text, a nested object or array as its JSON, except the array a declared repeated section is held
    /// in, whose items are that section's records — and its id is the Formbase document id, so a claim
    /// Eyu grounds in it points back at a document the instance can show. Each record carries the form
    /// type it was sampled from as its subject, so a field's declared role is read for that form type
    /// rather than guessed from its name.
    /// <para>
    /// Records, not appends. A document that names its record (<c>recordKey</c>) replaces that record's
    /// earlier documents, and a retirement removes the record — the same fold Formbase's own projection
    /// applies — so a corrected record is sampled once, as corrected, and a retired one not at all. A
    /// document without a key is a record of its own; an instance that predates record keys sends none,
    /// and every document is sampled as before. Reading stops once <paramref name="maxCount"/> records
    /// stand, so a record corrected or retired further along the stream is sampled as it stood there.
    /// </para>
    /// <para>
    /// A repeated section has no documents of its own: its declaration names the parent
    /// (<c>sectionOf</c>), and its records are cut from the parent's standing documents the way the
    /// projection cuts its rows — one per item of the section array, the item's members as fields, and
    /// the parent's value of the key field over an item member of the same name (the parent's record
    /// identity when the parent has no such field). A row's id is the document's, a slash and the item's
    /// position, the name the projection gives the row. In a section of values an item that is a value
    /// is the section's one field beside the key. Up to <paramref name="maxCount"/> parent documents are
    /// read, and up to <paramref name="maxCount"/> rows returned.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<RawRecord>> SampleAsync(SubjectRef subject, int maxCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxCount);

        var declaration = await ReadDeclarationAsync(subject, cancellationToken).ConfigureAwait(false);
        if (declaration?.SectionOf is { } section)
        {
            var parent = SubjectRef.Create(section.FormType);
            var valueField = (declaration.Fields ?? []).Select(f => f.Name).Where(f => f != section.KeyField).ToList() is [var only] ? only : null;
            return [.. (await StandingAsync(parent, maxCount, cancellationToken).ConfigureAwait(false))
                .SelectMany(document => Rows(document, section, valueField)
                    .Select((row, position) => new RawRecord($"{document.DocumentId}/{position.ToString(CultureInfo.InvariantCulture)}", row, subject)))
                .Take(maxCount)];
        }

        // A section array is not a field of its parent: its items are the section's records, and the
        // parent's projected table does not carry it.
        var sectionFields = (declaration?.Relations ?? []).Select(r => r.SectionField).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return [.. (await StandingAsync(subject, maxCount, cancellationToken).ConfigureAwait(false))
            .Select(document => new RawRecord(
                document.DocumentId.ToString(),
                Flatten(document.Body).Where(f => !sectionFields.Contains(f.Key)).ToDictionary(),
                subject))];
    }

    /// <summary>
    /// The rows a parent document's section array holds, by position. A missing or non-array section
    /// holds none, and so does a parent with an item that is neither an object nor, in a section of
    /// values, a value — the projection skips that parent, and its rows are read nowhere.
    /// </summary>
    private static List<Dictionary<string, string?>> Rows(DocumentResponse document, SectionOfResponse section, string? valueField)
    {
        if (document.Body.ValueKind != JsonValueKind.Object
            || !document.Body.TryGetProperty(section.SectionField, out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var key = Flatten(document.Body).TryGetValue(section.KeyField, out var value) ? value : document.RecordKey ?? document.DocumentId.ToString();
        var rows = new List<Dictionary<string, string?>>();
        foreach (var item in items.EnumerateArray())
        {
            Dictionary<string, string?>? row = item.ValueKind switch
            {
                JsonValueKind.Object => Flatten(item),
                JsonValueKind.Array or JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ when valueField is not null => new() { [valueField] = item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText() },
                _ => null,
            };
            if (row is null)
            {
                return [];
            }

            row[section.KeyField] = key;
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// The standing documents of <paramref name="formType"/>, oldest first, up to <paramref name="maxCount"/>
    /// — the fold described on <see cref="SampleAsync"/>.
    /// </summary>
    private async Task<List<DocumentResponse>> StandingAsync(SubjectRef formType, int maxCount, CancellationToken cancellationToken)
    {
        var subject = formType;

        // Standing records by position in the stream: the latest document of each key, and every document
        // without one. A later document of a key moves the record to that document's position.
        var standing = new SortedDictionary<long, DocumentResponse>();
        var latestByKey = new Dictionary<string, long>(StringComparer.Ordinal);
        long after = 0;
        while (standing.Count < maxCount)
        {
            var limit = Math.Min(maxCount - standing.Count, MaxPageSize);
            using var response = await SendAsync(
                $"formtypes/{Uri.EscapeDataString(subject.Value)}/documents?after={after.ToString(CultureInfo.InvariantCulture)}&limit={limit.ToString(CultureInfo.InvariantCulture)}",
                cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
            {
                throw new FormbaseConnectorException(
                    "This Formbase instance does not serve a form type's raw stream (GET /formtypes/{type}/documents answered 405). " +
                    "Reading documents across the process boundary needs Formbase 0.11.0 or later, which serves it.");
            }

            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            var page = await response.Content.ReadFromJsonAsync<DocumentPageResponse>(Json, cancellationToken).ConfigureAwait(false)
                ?? throw new FormbaseConnectorException($"Formbase answered a page of '{subject}' with an empty body.");

            // The whole page is folded, even past maxCount: a later document on it may correct or retire a
            // record already counted.
            foreach (var document in page.Documents)
            {
                if (document.RecordKey is { } key)
                {
                    if (latestByKey.Remove(key, out var earlier))
                    {
                        standing.Remove(earlier);
                    }

                    if (document.Retired)
                    {
                        continue;
                    }

                    latestByKey[key] = document.Watermark;
                }

                standing[document.Watermark] = document;
            }

            if (page.Documents.Count == 0 || page.Documents[^1].Watermark >= page.RawHead)
            {
                break;
            }

            after = page.Documents[^1].Watermark;
        }

        return [.. standing.Values.Take(maxCount)];
    }

    private async Task<HttpResponseMessage> SendAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (formbaseNamespace is not null)
        {
            request.Headers.Add(NamespaceHeader, formbaseNamespace);
        }

        return await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw Refused(response, await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>The body is read once, by the caller, and passed in — a response's content cannot be read twice.</summary>
    private static FormbaseConnectorException Refused(HttpResponseMessage response, ProblemResponse? problem)
    {
        var detail = problem is null ? string.Empty : $" {problem.Type}: {problem.Detail}";
        return new FormbaseConnectorException(
            $"Formbase answered {(int)response.StatusCode} to {response.RequestMessage?.RequestUri}.{detail}",
            response.StatusCode);
    }

    private static async Task<ProblemResponse?> ReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ProblemResponse>(Json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static Dictionary<string, string?> Flatten(JsonElement body)
    {
        var fields = new Dictionary<string, string?>();
        if (body.ValueKind != JsonValueKind.Object)
        {
            return fields;
        }

        foreach (var property in body.EnumerateObject())
        {
            fields[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => property.Value.GetString(),
                _ => property.Value.GetRawText(),
            };
        }

        return fields;
    }

    private static DeclaredValueKind ToDeclaredKind(string type) => type switch
    {
        "text" => DeclaredValueKind.Text,
        "integer" => DeclaredValueKind.WholeNumber,
        "decimal" => DeclaredValueKind.FractionalNumber,
        "boolean" => DeclaredValueKind.Boolean,
        "timestamp" => DeclaredValueKind.Timestamp,
        "date" => DeclaredValueKind.Date,
        "uuid" => DeclaredValueKind.Identifier,
        "jsonb" => DeclaredValueKind.Structured,
        _ => throw new FormbaseConnectorException($"Formbase declared a column type this connector does not know how to carry: '{type}'."),
    };

    private static DeclaredRelationKind ToRelationKind(string? kind) => kind switch
    {
        "reference" => DeclaredRelationKind.Reference,
        "child" => DeclaredRelationKind.Child,
        _ => throw new FormbaseConnectorException($"Formbase declared a relation kind this connector does not know how to carry: '{kind}'."),
    };

    /// <param name="SectionOf">The parent a repeated section's rows are cut from; absent for a form type with documents of its own, and from instances before 0.33.0.</param>
    private sealed record DeclarationResponse(
        long DeclarationVersion,
        IReadOnlyList<FieldResponse>? Fields,
        IReadOnlyList<RelationResponse>? Relations,
        SectionOfResponse? SectionOf = null);

    private sealed record SectionOfResponse(string FormType, string Relation, string SectionField, string KeyField);

    private sealed record FieldResponse(string Name, string Type, bool Nullable, TargetResponse? Target = null);

    /// <param name="ViaField">The field of this declaration carrying the target record's key; absent when the binding names none.</param>
    private sealed record TargetResponse(string FormType, string? ViaField = null);

    private sealed record RelationResponse(string Name, string? Kind, string Target, string? KeyField, string? SectionField = null);

    private sealed record DocumentPageResponse(IReadOnlyList<DocumentResponse> Documents, long RawHead);

    /// <param name="RecordKey">The record the document belongs to; absent from instances that predate record keys.</param>
    /// <param name="Retired">Whether the document retires <paramref name="RecordKey"/>; absent means it does not.</param>
    private sealed record DocumentResponse(Guid DocumentId, long Watermark, JsonElement Body, string? RecordKey = null, bool Retired = false);

    private sealed record ProblemResponse(string? Type, string? Detail);
}
